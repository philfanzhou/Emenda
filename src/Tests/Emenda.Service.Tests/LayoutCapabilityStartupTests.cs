namespace Emenda.Service.Tests;

/// <summary>
/// IKQA2N startup-snapshot acceptance tests. They run the real Mistake Host as a subprocess
/// (LocalBootstrapPostgresTests.RunHostAsync) and assert the capability-switch behavior:
/// the default-off switch, the strict bool gate, the enabled configuration requirements
/// before any database access, the masked credential and budget evidence in the startup
/// logs, and that an enabled or disabled startup sends zero layout requests (parent
/// criterion 7, configuration half).
/// </summary>
public sealed class LayoutCapabilityStartupTests
{
    private const string LayoutSecret = "layout-test-secret-key-42";

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Mistake__ConfigMode"] = "Local",
        ["ConnectionStrings__MistakeLocal"] =
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Password=db-secret-marker",
        ["Mistake__Layout__Enabled"] = null,
        ["Mistake__Layout__BaseUrl"] = null,
        ["Mistake__Layout__ApiKey"] = null,
    };

    [Fact]
    public async Task DefaultOffKeepsHostRunnableWithPlaceholderAddress()
    {
        // The connection string points at a refused port: the host must complete the layout
        // wiring (default off, placeholder address, no budget claims) and only fail later at
        // the unavailable database.
        var result = await LocalBootstrapPostgresTests.RunHostAsync([], BaseSettings());
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Layout detection: Enabled=False", result.Output);
        Assert.Contains("http://structadoc-layout-disabled.invalid", result.Output);
        Assert.DoesNotContain("Layout HTTP budget", result.Output);
        Assert.DoesNotContain("Mistake:Layout:BaseUrl", result.Output);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("")]
    public async Task InvalidSwitchValueFailsStartupWithKeyNameAsync(string value)
    {
        var settings = BaseSettings();
        settings["Mistake__Layout__Enabled"] = value;
        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Mistake:Layout:Enabled must be true or false.", result.Output);
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
    }

    [Theory]
    [InlineData(null, "Mistake:Layout:BaseUrl is required when Mistake:Layout:Enabled=true.")]
    [InlineData(" ", "Mistake:Layout:BaseUrl is required when Mistake:Layout:Enabled=true.")]
    [InlineData("not a url", "Mistake:Layout:BaseUrl must be an absolute HTTP(S) base URL without credentials, path, query or fragment.")]
    [InlineData("http://127.0.0.1:9/path", "Mistake:Layout:BaseUrl must be an absolute HTTP(S) base URL without credentials, path, query or fragment.")]
    [InlineData("http://user:pass@127.0.0.1:9", "Mistake:Layout:BaseUrl must be an absolute HTTP(S) base URL without credentials, path, query or fragment.")]
    [InlineData("ftp://127.0.0.1:9", "Mistake:Layout:BaseUrl must be an absolute HTTP(S) base URL without credentials, path, query or fragment.")]
    public async Task EnabledRequiresAValidAdministratorBaseUrlAsync(string? baseUrl, string diagnostic)
    {
        var settings = BaseSettings();
        settings["Mistake__Layout__Enabled"] = "true";
        settings["Mistake__Layout__BaseUrl"] = baseUrl;
        settings["Mistake__Layout__ApiKey"] = LayoutSecret;
        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Output);
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
    }

    [Fact]
    public async Task EnabledRequiresTheScopedCredentialAsync()
    {
        var settings = BaseSettings();
        settings["Mistake__Layout__Enabled"] = "true";
        settings["Mistake__Layout__BaseUrl"] = "http://127.0.0.1:9";
        settings["Mistake__Layout__ApiKey"] = null;
        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Mistake:Layout:ApiKey is required when Mistake:Layout:Enabled=true.", result.Output);
        Assert.DoesNotContain("Database: PostgreSQL", result.Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public async Task EnabledRejectsOutOfBoundBudgetsAsync(int budgetSeconds)
    {
        var settings = BaseSettings();
        settings["Mistake__Layout__Enabled"] = "true";
        settings["Mistake__Layout__BaseUrl"] = "http://127.0.0.1:9";
        settings["Mistake__Layout__ApiKey"] = LayoutSecret;
        settings["Mistake__Layout__PerImageBudgetSeconds"] = budgetSeconds.ToString();
        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Mistake:Layout:PerImageBudgetSeconds must be an integer from 1 to 3600.", result.Output);
    }

    [Fact]
    public async Task EnabledStartupLogsMaskedCredentialBudgetAndInfiniteTimeoutWithoutRequests()
    {
        // The StructaDoc base URL points at a request-counting probe: a correct startup
        // wires the client without sending anything to it.
        await using var structaDoc = new LocalBootstrapPostgresTests.RequestProbe();
        var settings = BaseSettings();
        settings["Mistake__Layout__Enabled"] = "true";
        settings["Mistake__Layout__BaseUrl"] = $"http://127.0.0.1:{structaDoc.Port}";
        settings["Mistake__Layout__ApiKey"] = LayoutSecret;
        var result = await LocalBootstrapPostgresTests.RunHostAsync([], settings);

        // The host proceeds through the wiring and stops at the unavailable database.
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Layout detection: Enabled=True", result.Output);
        // Credential masked through SensitiveDataMasker, never printed in clear.
        Assert.Contains("ApiKey=layo****y-42", result.Output);
        Assert.DoesNotContain(LayoutSecret, result.Output);
        // Parent criterion 3 evidence: the wired budget and the infinite HttpClient timeout
        // that leaves the 120-second cloud deadline to the per-image budget.
        Assert.Contains("Layout HTTP budget: PerImageBudgetSeconds=180, MaxConcurrentExchanges=4", result.Output);
        Assert.Contains("HttpClientTimeout=Infinite", result.Output);
        // Zero requests at startup, enabled or not.
        Assert.Equal(0, structaDoc.RequestCount);
    }
}
