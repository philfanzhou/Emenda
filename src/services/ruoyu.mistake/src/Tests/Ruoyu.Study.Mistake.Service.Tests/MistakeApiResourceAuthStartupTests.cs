namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJEPZ startup-snapshot acceptance tests. They run the real Mistake Host
/// as a subprocess (LocalBootstrapPostgresTests.RunHostAsync) and assert the
/// failure point: resource authorization requires the strict switch, invalid
/// values fail with the configuration key only, and the enabled combination
/// keeps validating the trust snapshot exactly like slice 1.
/// </summary>
public sealed class MistakeApiResourceAuthStartupTests
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
            ["IdentityService__MistakeApiResourceAuth"] = null,
            ["IDENTITY_APP_ID_ADMIN"] = null,
        };
    }

    [Fact]
    public async Task ResourceAuthWithoutStrictAuthFailsStartupWithConfigurationKeysOnlyAsync()
    {
        // resource=true presupposes an authenticated caller; the anonymous
        // compatibility window (strict=false) must not combine with it.
        var settings = BaseSettings();
        settings["IdentityService__MistakeApiStrictAuth"] = "false";
        settings["IdentityService__MistakeApiResourceAuth"] = "true";

        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "IdentityService:MistakeApiResourceAuth=true requires IdentityService:MistakeApiStrictAuth=true",
            result.Output);
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
        Assert.DoesNotContain("db-secret-marker", result.Output);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("")]
    public async Task InvalidResourceSwitchValueFailsStartupWithKeyNameAsync(string value)
    {
        var settings = BaseSettings();
        settings["IdentityService__MistakeApiResourceAuth"] = value;

        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("IdentityService:MistakeApiResourceAuth must be true or false.", result.Output);
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
    }

    [Theory]
    [InlineData(null, null, null,
        new[] { "IdentityService:Authority must be a non-empty absolute URI.",
                "IdentityService:Issuer must be a non-empty absolute HTTP or HTTPS URI.",
                "IdentityService:Audience is required." })]
    [InlineData("http://127.0.0.1:5002", "http://127.0.0.1:5002", null,
        new[] { "IdentityService:Audience is required." })]
    public async Task ResourceAuthWithStrictAndIncompleteTrustFailsStartupAsync(
        string? authority, string? issuer, string? audience, string[] diagnostics)
    {
        var settings = BaseSettings();
        settings["IdentityService__MistakeApiStrictAuth"] = "true";
        settings["IdentityService__MistakeApiResourceAuth"] = "true";
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
    }

    [Fact]
    public async Task ResourceAuthWithStrictAndFullTrustPassesAuthWiringAsync()
    {
        // The valid combination passes the whole authentication wiring and
        // stops at the next Local-mode gate (explicit connection string),
        // never contacting the Authority for JWKS metadata.
        await using var authority = new LocalBootstrapPostgresTests.RequestProbe();
        var settings = BaseSettings();
        settings["ConnectionStrings__MistakeLocal"] = null;
        settings["IdentityService__MistakeApiStrictAuth"] = "true";
        settings["IdentityService__MistakeApiResourceAuth"] = "true";
        settings["IdentityService__RequireHttpsMetadata"] = "false";
        settings["IdentityService__Authority"] = $"http://127.0.0.1:{authority.Port}";
        settings["IdentityService__Issuer"] = $"http://127.0.0.1:{authority.Port}";
        settings["IdentityService__Audience"] = "QuantumZhou.microservices";

        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Local mode requires ConnectionStrings:MistakeLocal", result.Output);
        Assert.DoesNotContain("IdentityService:MistakeApiResourceAuth", result.Output);
        Assert.Equal(0, authority.RequestCount);
    }

    [Fact]
    public async Task ResourceAuthOffByDefaultKeepsCompatibilityWindowRunnableAsync()
    {
        // resource explicitly false with strict=false: no IdentityService
        // section is required, the anonymous window keeps the host runnable
        // up to the Local database gate.
        var settings = BaseSettings();
        settings["ConnectionStrings__MistakeLocal"] = null;
        settings["IdentityService__MistakeApiResourceAuth"] = "false";

        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Local mode requires ConnectionStrings:MistakeLocal", result.Output);
        Assert.DoesNotContain("IdentityService:MistakeApiResourceAuth must be true or false.", result.Output);
        Assert.DoesNotContain("OptionsValidationException", result.Output);
    }
}
