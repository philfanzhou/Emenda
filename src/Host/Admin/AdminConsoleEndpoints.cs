using System.Security.Cryptography;
using Emenda.Host.Authentication;
using Emenda.Host.Security;
using Microsoft.AspNetCore.Authorization;
using SignaCore.Client.AspNetCore;
using System.Text.Json;

namespace Emenda.Host.Admin;

/// <summary>
/// The admin console HTTP surface (standalone deployments only):
/// <list type="bullet">
/// <item><c>GET /admin</c> — the single console page (anonymous; it decides
/// login state through the session endpoint).</item>
/// <item>the SignaCore hosted-login protocol endpoints under
/// <c>/admin/auth/oidc/*</c> (anonymous; owned by the official client
/// package), or fixed 503 stubs when the console is not configured.</item>
/// <item><c>/admin/api/callers*</c> — the caller-credential management API,
/// protected by the console session policy; unsafe methods carry the CSRF
/// header.</item>
/// </list>
/// </summary>
public static class AdminConsoleEndpoints
{
    public static WebApplication MapAdminConsole(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AdminConsoleSettings>();
        var page = AdminConsolePage.Render(AdminConsoleRenderState.For(
            app.Services.GetService<DynamicCallerStore>()?.Path));

        app.MapGet(AdminConsoleConstants.ConsolePath, (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Text(page, AdminConsolePage.MediaType);
        });

        if (!settings.Available)
        {
            var disabled = app.MapGroup(AdminConsoleConstants.Prefix);
            disabled.MapGet("/start", Disabled);
            disabled.MapGet("/callback", Disabled);
            disabled.MapGet("/csrf", Disabled);
            disabled.MapGet("/session", Disabled);
            disabled.MapPost("/logout", Disabled);
            disabled.MapGet("/logout/return", Disabled);
            return app;
        }

        app.MapGroup(string.Empty).MapSignaCoreHostedLogin(AdminConsoleConstants.Prefix);

        var api = app.MapGroup("/admin/api")
            .RequireAuthorization(AdminConsoleConstants.Policy);
        api.MapGet("/callers", ListCallers);
        api.MapPost("/callers", CreateCaller);
        api.MapGet("/callers/{name}/secret", RevealSecret);
        api.MapPost("/callers/{name}/rotate", RotateCaller);
        api.MapPut("/callers/{name}", UpdateCaller);
        api.MapDelete("/callers/{name}", DeleteCaller);
        return app;
    }

    private static IResult Disabled() => Results.Json(
        new { success = false, message = AdminConsoleConstants.NotConfiguredMessage },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    // ---- callers management ----

    private static async Task<IResult> ListCallers(CallerRegistry registry, CancellationToken ct)
    {
        var entries = await registry.ListAsync(ct).ConfigureAwait(false);
        return Results.Json(new
        {
            success = true,
            data = entries.Select(entry => new
            {
                name = entry.Name,
                maskedKey = Mask(entry.ApiKey),
                source = entry.Source == CallerEntrySource.Configuration ? "configuration" : "dynamic",
                enabled = entry.Enabled,
                note = entry.Note,
                createdAtUtc = entry.CreatedAtUtc,
                updatedAtUtc = entry.UpdatedAtUtc
            })
        });
    }

    private static async Task<IResult> CreateCaller(
        CallerRegistry registry,
        HttpContext http,
        CancellationToken ct)
    {
        var body = await ReadJsonBody(http, ct).ConfigureAwait(false);
        var name = BodyString(body, "name");
        var note = BodyString(body, "note") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return Results.Json(new { success = false, message = "name 是必填项。" }, statusCode: 400);
        var store = registry.DynamicStore;
        if (store is null)
            return Results.Json(
                new { success = false, message = "未配置动态凭据文件（Emenda:Security:DynamicCallers:Path）。" },
                statusCode: 503);
        try
        {
            var entry = await store.UpsertAsync(name, GenerateKey(), Actor(http), note, ct).ConfigureAwait(false);
            return Results.Json(new { success = true, data = new { name = entry.Name, apiKey = entry.ApiKey } });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { success = false, message = ex.Message }, statusCode: 400);
        }
    }

    private static async Task<IResult> RevealSecret(
        string name, CallerRegistry registry, CancellationToken ct)
    {
        var entries = await registry.ListAsync(ct).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return Results.Json(new { success = false, message = "调用方不存在。" }, statusCode: 404);
        if (entry.Source != CallerEntrySource.Dynamic)
            return Results.Json(
                new { success = false, message = "静态配置条目的密钥由部署配置持有，不在控制台显示；可用轮换生成动态覆盖。" },
                statusCode: 409);
        return Results.Json(new { success = true, data = new { name = entry.Name, apiKey = entry.ApiKey } });
    }

    private static async Task<IResult> RotateCaller(
        string name, CallerRegistry registry, HttpContext http, CancellationToken ct)
    {
        var store = registry.DynamicStore;
        if (store is null)
            return Results.Json(
                new { success = false, message = "未配置动态凭据文件（Emenda:Security:DynamicCallers:Path）。" },
                statusCode: 503);
        var existing = (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            // Rotating a static entry creates the dynamic override with the same name.
            var created = await store.UpsertAsync(name, GenerateKey(), Actor(http), "rotated from configuration", ct)
                .ConfigureAwait(false);
            return Results.Json(new { success = true, data = new { name = created.Name, apiKey = created.ApiKey } });
        }
        var rotated = await store.UpsertAsync(existing.Name, GenerateKey(), Actor(http), existing.Note, ct)
            .ConfigureAwait(false);
        return Results.Json(new { success = true, data = new { name = rotated.Name, apiKey = rotated.ApiKey } });
    }

    private static async Task<IResult> UpdateCaller(
        string name, CallerRegistry registry, HttpContext http, CancellationToken ct)
    {
        var store = registry.DynamicStore;
        if (store is null)
            return Results.Json(
                new { success = false, message = "未配置动态凭据文件（Emenda:Security:DynamicCallers:Path）。" },
                statusCode: 503);
        var body = await ReadJsonBody(http, ct).ConfigureAwait(false);
        var enabled = BodyBool(body, "enabled");
        var note = BodyString(body, "note");
        var updated = await store.UpdateAsync(name, enabled, note, ct).ConfigureAwait(false);
        return updated is null
            ? Results.Json(new { success = false, message = "调用方不存在。" }, statusCode: 404)
            : Results.Json(new { success = true, data = new { name = updated.Name, enabled = updated.Enabled } });
    }

    private static async Task<IResult> DeleteCaller(
        string name, CallerRegistry registry, CancellationToken ct)
    {
        var store = registry.DynamicStore;
        if (store is null)
            return Results.Json(
                new { success = false, message = "未配置动态凭据文件（Emenda:Security:DynamicCallers:Path）。" },
                statusCode: 503);
        var removed = await store.RemoveAsync(name, ct).ConfigureAwait(false);
        return removed is null
            ? Results.Json(new { success = false, message = "调用方不存在。" }, statusCode: 404)
            : Results.Json(new { success = true, data = new { name = removed.Name } });
    }

    // ---- helpers ----

    private static async Task<JsonDocument?> ReadJsonBody(HttpContext http, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(http.Request.Body);
            var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static string? BodyString(JsonDocument? body, string property)
    {
        if (body is null) return null;
        return body.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? BodyBool(JsonDocument? body, string property)
    {
        if (body is null) return null;
        return body.RootElement.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static string Actor(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "admin";

    private static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Mask(string key) =>
        key.Length <= 8 ? "****" : key[..4] + "****" + key[^4..];
}
