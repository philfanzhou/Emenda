using Npgsql;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJEIC startup-snapshot acceptance tests. They run the real Mistake Host
/// as a subprocess (LocalBootstrapPostgresTests.RunHostAsync) and assert the
/// failure point: strict=true with a missing or incomplete trust snapshot
/// fails startup with configuration-key-only messages before any database
/// access, while strict=false keeps the host running past the authentication
/// wiring without an IdentityService section and without contacting the
/// Authority for JWKS metadata.
/// </summary>
public sealed class MistakeApiStrictAuthStartupTests
{
    private static Dictionary<string, string?> BaseSettings()
    {
        // Hermetic by default: no inherited trust keys, Local mode.
        return new Dictionary<string, string?>
        {
            ["Mistake__ConfigMode"] = "Local",
            ["ConnectionStrings__MistakeLocal"] =
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Password=db-secret-marker",
            ["IdentityService__Authority"] = null,
            ["IdentityService__Issuer"] = null,
            ["IdentityService__Audience"] = null,
            ["IdentityService__AdminAudience"] = null,
            ["IdentityService__MistakeApiStrictAuth"] = null,
            ["IDENTITY_APP_ID_ADMIN"] = null,
        };
    }

    [Theory]
    [InlineData(null, null, null,
        new[] { "IdentityService:Authority must be a non-empty absolute URI.",
                "IdentityService:Issuer must be a non-empty absolute HTTP or HTTPS URI.",
                "IdentityService:Audience is required." })]
    [InlineData("http://127.0.0.1:5002", null, null,
        new[] { "IdentityService:Issuer must be a non-empty absolute HTTP or HTTPS URI.",
                "IdentityService:Audience is required." })]
    [InlineData("http://127.0.0.1:5002", "http://127.0.0.1:5002", null,
        new[] { "IdentityService:Audience is required." })]
    public async Task StrictModeWithIncompleteTrustConfigFailsStartupBeforeDatabaseAsync(
        string? authority, string? issuer, string? audience, string[] diagnostics)
    {
        var settings = BaseSettings();
        settings["IdentityService__MistakeApiStrictAuth"] = "true";
        settings["IdentityService__RequireHttpsMetadata"] = "false";
        if (authority is not null) settings["IdentityService__Authority"] = authority;
        if (issuer is not null) settings["IdentityService__Issuer"] = issuer;
        if (audience is not null) settings["IdentityService__Audience"] = audience;

        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        Assert.NotEqual(0, result.ExitCode);
        foreach (var diagnostic in diagnostics)
        {
            Assert.Contains(diagnostic, result.Output);
        }
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
        Assert.DoesNotContain("db-secret-marker", result.Output);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("")]
    public async Task InvalidStrictSwitchValueFailsStartupWithKeyNameAsync(string value)
    {
        var settings = BaseSettings();
        settings["IdentityService__MistakeApiStrictAuth"] = value;

        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("IdentityService:MistakeApiStrictAuth must be true or false.", result.Output);
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
    }

    [Fact]
    public async Task CompatibilityWindowWithoutIdentityServiceConfigKeepsHostRunnableAsync()
    {
        // No database connection string either: the host must pass the
        // authentication wiring (no validation, no JWKS) and stop at the
        // next Local-mode gate, which is the explicit connection string
        // requirement. No IdentityService error appears.
        var settings = BaseSettings();
        settings["ConnectionStrings__MistakeLocal"] = null;

        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Local mode requires ConnectionStrings:MistakeLocal", result.Output);
        Assert.DoesNotContain("IdentityService:Authority", result.Output);
        Assert.DoesNotContain("IdentityService:Issuer", result.Output);
        Assert.DoesNotContain("IdentityService:Audience", result.Output);
        Assert.DoesNotContain("OptionsValidationException", result.Output);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task StartupDoesNotContactAuthorityForJwksMetadataAsync(string strict)
    {
        await using var authority = new LocalBootstrapPostgresTests.RequestProbe();
        var settings = BaseSettings();
        settings["ConnectionStrings__MistakeLocal"] = null;
        settings["IdentityService__MistakeApiStrictAuth"] = strict;
        settings["IdentityService__RequireHttpsMetadata"] = "false";
        settings["IdentityService__Authority"] = $"http://127.0.0.1:{authority.Port}";
        settings["IdentityService__Issuer"] = $"http://127.0.0.1:{authority.Port}";
        settings["IdentityService__Audience"] = "QuantumZhou.microservices";

        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        // Both modes validate the trust snapshot only; OIDC discovery/JWKS is
        // lazy (first token validation), so startup never contacts the
        // Authority. The run passes the full auth wiring and stops at the
        // Local database gate.
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Local mode requires ConnectionStrings:MistakeLocal", result.Output);
        Assert.Equal(0, authority.RequestCount);
    }
}
