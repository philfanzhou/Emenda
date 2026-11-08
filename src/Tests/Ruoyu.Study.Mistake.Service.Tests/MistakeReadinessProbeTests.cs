using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Host.Services.Health;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJFIX unit tests for the readiness dependency matrix: component set is the
/// startup capability snapshot (student/vl only when wired), oss probe has
/// success/failure states, HTTP probes treat any response as reachable and
/// time out independently, and concurrent probes are safe.
/// </summary>
public sealed class MistakeReadinessProbeTests
{
    private static MistakeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MistakeDbContext>()
            .UseInMemoryDatabase($"readiness-{Guid.NewGuid():N}").Options);

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private static Mock<IHttpClientFactory> CreateHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        // A fresh client per CreateClient call: the probe disposes each client
        // after probing, and the real factory never hands out disposed clients.
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(MistakeReadinessProbe.HttpClientName))
            .Returns(() => new HttpClient(new StubHttpMessageHandler(handler))
            {
                // Unit-level timeout shorter than the probe default so the timeout
                // path is exercised without a three-second wait.
                Timeout = TimeSpan.FromMilliseconds(300),
            });
        return factory;
    }

    private static Mock<IHttpClientFactory> ImmediateResponseFactory(HttpStatusCode statusCode) =>
        CreateHttpClientFactory((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));

    private static MistakeReadinessProbe CreateProbe(
        MistakeDbContext? context = null,
        IOssService? oss = null,
        Mock<IHttpClientFactory>? httpClientFactory = null,
        MistakeReadinessSnapshot? snapshot = null)
    {
        return new MistakeReadinessProbe(
            context ?? CreateContext(),
            oss ?? Mock.Of<IOssService>(),
            (httpClientFactory ?? new Mock<IHttpClientFactory>()).Object,
            snapshot ?? new MistakeReadinessSnapshot(false, string.Empty, false, string.Empty),
            NullLogger<MistakeReadinessProbe>.Instance);
    }

    [Fact]
    public async Task Matrix_ShouldContainOnlyWiredComponentsAsync()
    {
        var oss = new Mock<IOssService>();
        oss.Setup(x => x.CheckConnectivityAsync()).ReturnsAsync(true);
        var probe = CreateProbe(oss: oss.Object, snapshot: new MistakeReadinessSnapshot(false, string.Empty, false, string.Empty));

        var components = await probe.ProbeAsync(CancellationToken.None);

        components.Select(c => c.Name).Should().Equal("db", "oss");
        components.Should().OnlyContain(c => c.Ok);
    }

    [Fact]
    public async Task Matrix_ShouldIncludeStudentAndVlWhenWiredAsync()
    {
        var oss = new Mock<IOssService>();
        oss.Setup(x => x.CheckConnectivityAsync()).ReturnsAsync(true);
        var factory = ImmediateResponseFactory(HttpStatusCode.NotFound);
        var probe = CreateProbe(
            oss: oss.Object,
            httpClientFactory: factory,
            snapshot: new MistakeReadinessSnapshot(
                true, "http://student.local:5005", true, "http://vl.local/v1"));

        var components = await probe.ProbeAsync(CancellationToken.None);

        components.Select(c => c.Name).Should().Equal("db", "oss", "student", "vl");
        components.Should().OnlyContain(c => c.Ok);
    }

    [Fact]
    public async Task OssProbe_ShouldReportFailureWhenConnectivityReturnsFalseAsync()
    {
        var oss = new Mock<IOssService>();
        oss.Setup(x => x.CheckConnectivityAsync()).ReturnsAsync(false);
        var probe = CreateProbe(oss: oss.Object);

        var components = await probe.ProbeAsync(CancellationToken.None);

        var ossComponent = components.Single(c => c.Name == "oss");
        ossComponent.Ok.Should().BeFalse();
        ossComponent.Reason.Should().Be("connect failed");
    }

    [Fact]
    public async Task OssProbe_ShouldReportTimeoutWhenConnectivityNeverCompletesAsync()
    {
        var oss = new Mock<IOssService>();
        oss.Setup(x => x.CheckConnectivityAsync())
            .Returns(new TaskCompletionSource<bool>().Task); // never completes
        var probe = CreateProbe(oss: oss.Object);

        var components = await probe.ProbeAsync(CancellationToken.None);

        var ossComponent = components.Single(c => c.Name == "oss");
        ossComponent.Ok.Should().BeFalse();
        ossComponent.Reason.Should().Be("timeout");
    }

    [Fact]
    public async Task HttpProbe_ShouldTreatAnyResponseAsReachableAsync()
    {
        var factory = ImmediateResponseFactory(HttpStatusCode.ServiceUnavailable);
        var probe = CreateProbe(httpClientFactory: factory, snapshot: new MistakeReadinessSnapshot(
            true, "http://student.local:5005", false, string.Empty));

        var components = await probe.ProbeAsync(CancellationToken.None);

        var student = components.Single(c => c.Name == "student");
        student.Ok.Should().BeTrue();
        student.Reason.Should().BeNull();
    }

    [Fact]
    public async Task HttpProbe_ShouldReportConnectFailedOnRequestFailureAsync()
    {
        var factory = CreateHttpClientFactory((_, _) =>
            throw new HttpRequestException("connection refused"));
        var probe = CreateProbe(httpClientFactory: factory, snapshot: new MistakeReadinessSnapshot(
            true, "http://student.local:5005", false, string.Empty));

        var components = await probe.ProbeAsync(CancellationToken.None);

        var student = components.Single(c => c.Name == "student");
        student.Ok.Should().BeFalse();
        student.Reason.Should().Be("connect failed");
    }

    [Fact]
    public async Task HttpProbe_ShouldReportTimeoutWithoutHangingAsync()
    {
        var factory = CreateHttpClientFactory(async (_, cancellationToken) =>
        {
            // Observing the token lets the HttpClient timeout cancel the in-flight
            // request, exactly like the real transport handler does.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var probe = CreateProbe(httpClientFactory: factory, snapshot: new MistakeReadinessSnapshot(
            false, string.Empty, true, "http://vl.local/v1"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var components = await probe.ProbeAsync(timeout.Token);

        var vl = components.Single(c => c.Name == "vl");
        vl.Ok.Should().BeFalse();
        vl.Reason.Should().Be("timeout");
    }

    [Fact]
    public async Task Probe_ShouldBeSafeToRunConcurrentlyAsync()
    {
        var oss = new Mock<IOssService>();
        oss.Setup(x => x.CheckConnectivityAsync()).ReturnsAsync(true);
        var probe = CreateProbe(oss: oss.Object, snapshot: new MistakeReadinessSnapshot(
            false, string.Empty, false, string.Empty));

        var first = await probe.ProbeAsync(CancellationToken.None);
        var second = await probe.ProbeAsync(CancellationToken.None);

        first.Select(c => (c.Name, c.Ok, c.Reason))
            .Should().Equal(second.Select(c => (c.Name, c.Ok, c.Reason)));
    }

    [Fact]
    public async Task DbProbe_ShouldReportFailureWhenConnectionCannotOpenAsync()
    {
        // PostgreSQL context pointed at a closed port: EF Core's CanConnectAsync
        // opens a connection and reports false on connection failure, so the
        // failed-db component path is exercised without a live server.
        var closedPort = LocalBootstrapPostgresTests.GetAvailablePort();
        var context = new MistakeDbContext(new DbContextOptionsBuilder<MistakeDbContext>()
            .UseNpgsql($"Host=127.0.0.1;Port={closedPort};Database=readiness_probe_unreachable;Username=none;Password=none")
            .Options);
        var probe = CreateProbe(context: context);

        var components = await probe.ProbeAsync(CancellationToken.None);

        var db = components.Single(c => c.Name == "db");
        db.Ok.Should().BeFalse();
        db.Reason.Should().BeOneOf("connect failed", "timeout");
    }

    [Theory]
    [InlineData("valid", true, 2)]
    [InlineData("wrong-issuer", false, 1)]
    [InlineData("wrong-origin", false, 1)]
    [InlineData("unavailable", false, 1)]
    [InlineData("bad-json", false, 1)]
    [InlineData("empty-keys", false, 2)]
    public async Task StrictIdentityProbe_ShouldRequireActualDiscoveryAndKeysAsync(
        string scenario, bool expected, int expectedRequests)
    {
        var requests = 0;
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(MistakeReadinessProbe.IdentityHttpClientName))
            .Returns(() => new HttpClient(new StubHttpMessageHandler((request, _) =>
            {
                requests++;
                var discovery = request.RequestUri!.AbsolutePath.Contains("openid-configuration");
                var body = discovery
                    ? "{\"issuer\":\"https://identity.test\",\"jwks_uri\":\"https://identity.test/.well-known/jwks\"}"
                    : "{\"keys\":[{\"kty\":\"RSA\"}]}";
                if (scenario == "wrong-issuer") body = body.Replace("\"issuer\":\"https://identity.test\"", "\"issuer\":\"https://other.test\"");
                if (scenario == "wrong-origin") body = body.Replace("identity.test/.well-known", "other.test/.well-known");
                if (scenario == "bad-json") body = "not-json";
                if (scenario == "empty-keys" && !discovery) body = "{\"keys\":[]}";
                return Task.FromResult(new HttpResponseMessage(scenario == "unavailable"
                    ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(body) });
            })));
        var probe = CreateProbe(httpClientFactory: factory,
            snapshot: new MistakeReadinessSnapshot(false, "", false, "", true, "https://identity.test", "https://identity.test"));

        var components = await probe.ProbeAsync(CancellationToken.None);

        var issuer = components.Single(x => x.Name == "issuer");
        issuer.Ok.Should().Be(expected);
        issuer.Reason.Should().Be(expected ? null : "probe failed");
        requests.Should().Be(expectedRequests);
    }

    [Fact]
    public async Task StrictIdentityProbe_ShouldBoundTimeoutAndPropagateCallerCancellationAsync()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(MistakeReadinessProbe.IdentityHttpClientName))
            .Returns(() => new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })) { Timeout = TimeSpan.FromMilliseconds(50) });
        var probe = CreateProbe(httpClientFactory: factory,
            snapshot: new MistakeReadinessSnapshot(false, "", false, "", true, "https://identity.test", "https://identity.test"));

        (await probe.ProbeAsync(CancellationToken.None)).Single(x => x.Name == "issuer")
            .Should().Be(new MistakeReadinessComponent("issuer", false, "timeout"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions.Awaiting(() => probe.ProbeAsync(cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
