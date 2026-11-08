using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Emenda.Common.Oss;
using Emenda.Database;

namespace Emenda.Host.Services.Health;

/// <summary>
/// Startup snapshot of the readiness dependency matrix (IKJFIX): which optional
/// capabilities this process actually wired. The values mirror the startup
/// switches resolved in Program.cs and are fixed for the process lifetime, so
/// the set of probed components never changes while the process runs.
/// </summary>
public sealed record MistakeReadinessSnapshot(
    bool StudentEnabled,
    string StudentBaseUrl,
    bool VlConfigured,
    string VlBaseUrl,
    bool IdentityRequired = false,
    string IdentityAuthority = "",
    string IdentityIssuer = "",
    bool LayoutEnabled = false,
    string LayoutBaseUrl = "");

/// <summary>
/// One readiness component outcome. <see cref="Reason"/> uses a fixed short
/// taxonomy ("timeout" / "connect failed" / "probe failed") and never raw
/// exception text, endpoint addresses, connection strings or presigned URLs.
/// </summary>
public sealed record MistakeReadinessComponent(string Name, bool Ok, string? Reason = null);

/// <summary>
/// Readiness dependency probe (IKJFIX). Probes each component of the startup
/// capability snapshot with an independent short timeout: db via an EF Core
/// lightweight connectivity check, oss via an S3 bucket HEAD (authorization
/// probe), student/vl via a short-timeout HTTP request to the resolved base
/// URL where any HTTP response counts as reachable. Probes are read-only and
/// idempotent, safe to run concurrently.
/// </summary>
public sealed class MistakeReadinessProbe
{
    public const string HttpClientName = "mistake-readiness-probe";
    public const string IdentityHttpClientName = "mistake-identity-readiness-probe";
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly MistakeDbContext _dbContext;
    private readonly IOssService _ossService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MistakeReadinessSnapshot _snapshot;
    private readonly ILogger<MistakeReadinessProbe> _logger;

    public MistakeReadinessProbe(
        MistakeDbContext dbContext,
        IOssService ossService,
        IHttpClientFactory httpClientFactory,
        MistakeReadinessSnapshot snapshot,
        ILogger<MistakeReadinessProbe> logger)
    {
        _dbContext = dbContext;
        _ossService = ossService;
        _httpClientFactory = httpClientFactory;
        _snapshot = snapshot;
        _logger = logger;
    }

    public async Task<List<MistakeReadinessComponent>> ProbeAsync(CancellationToken cancellationToken)
    {
        var components = new List<MistakeReadinessComponent>
        {
            await ProbeDatabaseAsync(cancellationToken).ConfigureAwait(false),
            await ProbeOssAsync(cancellationToken).ConfigureAwait(false),
        };

        if (_snapshot.StudentEnabled)
        {
            components.Add(await ProbeHttpEndpointAsync(
                "student", _snapshot.StudentBaseUrl, cancellationToken).ConfigureAwait(false));
        }

        if (_snapshot.VlConfigured)
        {
            components.Add(await ProbeHttpEndpointAsync(
                "vl", _snapshot.VlBaseUrl, cancellationToken).ConfigureAwait(false));
        }

        if (_snapshot.IdentityRequired)
        {
            components.Add(await ProbeIdentityAsync(cancellationToken).ConfigureAwait(false));
        }

        if (_snapshot.LayoutEnabled)
        {
            components.Add(await ProbeHttpEndpointAsync(
                "structadoc", _snapshot.LayoutBaseUrl, cancellationToken).ConfigureAwait(false));
        }

        return components;
    }

    private async Task<MistakeReadinessComponent> ProbeIdentityAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var client = _httpClientFactory.CreateClient(IdentityHttpClientName);
            var authority = new Uri(_snapshot.IdentityAuthority.TrimEnd('/') + "/");
            using var discoveryResponse = await client.GetAsync(
                new Uri(authority, ".well-known/openid-configuration"), timeout.Token).ConfigureAwait(false);
            discoveryResponse.EnsureSuccessStatusCode();
            using var discovery = JsonDocument.Parse(await discoveryResponse.Content
                .ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var root = discovery.RootElement;
            if (root.GetProperty("issuer").GetString() != _snapshot.IdentityIssuer
                || !Uri.TryCreate(root.GetProperty("jwks_uri").GetString(), UriKind.Absolute, out var jwksUri)
                || jwksUri.Scheme != authority.Scheme || jwksUri.Authority != authority.Authority)
                return Log(new MistakeReadinessComponent("issuer", false, "probe failed"));
            using var keysResponse = await client.GetAsync(jwksUri, timeout.Token).ConfigureAwait(false);
            keysResponse.EnsureSuccessStatusCode();
            using var keys = JsonDocument.Parse(await keysResponse.Content
                .ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var available = keys.RootElement.GetProperty("keys");
            if (available.ValueKind != JsonValueKind.Array || available.GetArrayLength() == 0)
                return Log(new MistakeReadinessComponent("issuer", false, "probe failed"));
            return Log(new MistakeReadinessComponent("issuer", true));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Log(new MistakeReadinessComponent("issuer", false, "timeout"));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Log(new MistakeReadinessComponent("issuer", false, "probe failed"));
        }
    }

    private async Task<MistakeReadinessComponent> ProbeDatabaseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            // Lightweight EF Core connectivity probe (opens a connection / SELECT 1
            // under the hood); returns false instead of throwing on failure.
            var ok = await _dbContext.Database.CanConnectAsync(timeout.Token).ConfigureAwait(false);
            var component = ok
                ? new MistakeReadinessComponent("db", true)
                : new MistakeReadinessComponent("db", false, "connect failed");
            Log(component);
            return component;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Log(new MistakeReadinessComponent("db", false, "timeout"));
        }
        catch (Exception)
        {
            return Log(new MistakeReadinessComponent("db", false, "probe failed"));
        }
    }

    private async Task<MistakeReadinessComponent> ProbeOssAsync(CancellationToken cancellationToken)
    {
        // CheckConnectivityAsync is a bucket HEAD (authorization probe) that returns
        // false instead of throwing; it takes no cancellation token, so it is raced
        // against the shared short timeout. A timed-out probe keeps running in the
        // background and its eventual outcome is discarded.
        try
        {
            var ok = await WithTimeoutAsync(_ossService.CheckConnectivityAsync(), cancellationToken).ConfigureAwait(false);
            var component = ok
                ? new MistakeReadinessComponent("oss", true)
                : new MistakeReadinessComponent("oss", false, "connect failed");
            Log(component);
            return component;
        }
        catch (TimeoutException)
        {
            return Log(new MistakeReadinessComponent("oss", false, "timeout"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Log(new MistakeReadinessComponent("oss", false, "timeout"));
        }
        catch (Exception)
        {
            return Log(new MistakeReadinessComponent("oss", false, "probe failed"));
        }
    }

    private async Task<MistakeReadinessComponent> ProbeHttpEndpointAsync(
        string name, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client
                .GetAsync(baseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            // Any HTTP response proves the endpoint is reachable and answering;
            // the business surface is versioned separately and not judged here.
            var reachable = new MistakeReadinessComponent(name, true);
            Log(reachable);
            return reachable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient throws TaskCanceledException on its own Timeout; the
            // outer token being live distinguishes that from caller cancellation.
            return Log(new MistakeReadinessComponent(name, false, "timeout"));
        }
        catch (Exception)
        {
            return Log(new MistakeReadinessComponent(name, false, "connect failed"));
        }
    }

    private async Task<T> WithTimeoutAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        var delay = Task.Delay(ProbeTimeout, cancellationToken);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException();
        }
        return await task.ConfigureAwait(false);
    }

    private MistakeReadinessComponent Log(MistakeReadinessComponent component)
    {
        // Component name and fixed reason taxonomy only; no addresses or secrets.
        _logger.LogInformation(
            "Readiness probe: Component={Component}, Ok={Ok}, Reason={Reason}",
            component.Name, component.Ok, component.Reason ?? "none");
        return component;
    }
}
