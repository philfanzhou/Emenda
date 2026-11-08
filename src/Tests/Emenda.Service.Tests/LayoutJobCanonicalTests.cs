using Emenda.Host.Services.Layout;

namespace Emenda.Service.Tests;

/// <summary>Pure-logic coverage of the layout job pipeline helpers (IKQA2O): the
/// captured non-secret configuration, bounded diagnostics, retry eligibility, backoff
/// (including honored upstream Retry-After hints) and the relevant source rotation
/// lookup.</summary>
public sealed class LayoutJobCanonicalTests
{
    private static StructaDocLayoutOptions Options(string? apiKey = "secret-layout-key-value") => new()
    {
        BaseUrl = "http://structadoc.internal/",
        ApiKey = apiKey
    };

    [Fact]
    public void CapturedConfigCarriesNoCredential()
    {
        var config = LayoutJobCanonical.CaptureConfig(Options());

        Assert.DoesNotContain("secret-layout-key-value", config, StringComparison.Ordinal);
        using var document = System.Text.Json.JsonDocument.Parse(config);
        var root = document.RootElement;
        // No credential property exists; the stored credential reference names only
        // the configuration key that holds the secret.
        Assert.DoesNotContain(root.EnumerateObject(), property =>
            property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) && property.Name != "credentialReference");
        Assert.Equal("mistake-layout-v1", root.GetProperty("contractId").GetString());
        Assert.Equal("Mistake:Layout:ApiKey", root.GetProperty("credentialReference").GetString());
        Assert.Equal("top-left-origin-normalized", root.GetProperty("coordinateFrame").GetString());
        Assert.Equal("http://structadoc.internal/", root.GetProperty("baseUrl").GetString());
        Assert.Equal(StructaDocLayoutOptions.DefaultPerImageBudgetSeconds,
            root.GetProperty("perImageBudgetSeconds").GetInt32());
        Assert.Equal(StructaDocLayoutOptions.DefaultMaxConcurrentExchanges,
            root.GetProperty("maxConcurrentExchanges").GetInt32());
        Assert.Equal(StructaDocLayoutOptions.DefaultMaxResponseBytes,
            root.GetProperty("maxResponseBytes").GetInt32());
        Assert.Equal(7, root.EnumerateObject().Count());
    }

    [Fact]
    public void RequestCanonicalBindsContractVersionSourceAndRevision()
    {
        var source = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var canonical = LayoutJobCanonical.Request(source, revision);
        Assert.Equal(canonical, LayoutJobCanonical.Request(source, revision));
        Assert.NotEqual(canonical, LayoutJobCanonical.Request(source, Guid.NewGuid()));
        Assert.NotEqual(canonical, LayoutJobCanonical.Request(Guid.NewGuid(), revision));
    }

    [Fact]
    public void DiagnosticsAreBoundedInCountAndLength()
    {
        var bounded = LayoutJobCanonical.BoundDiagnostics(
            Enumerable.Range(0, 20).Select(i => new string('d', 250 + i)).ToList());
        Assert.Equal(LayoutJobCanonical.MaxDiagnostics, bounded.Count);
        Assert.All(bounded, entry => Assert.Equal(LayoutJobCanonical.MaxDiagnosticLength, entry.Length));
        Assert.Equal([], LayoutJobCanonical.BoundDiagnostics([]));
    }

    [Fact]
    public void ErrorKindsAreBounded()
    {
        Assert.Equal("Overloaded", LayoutJobCanonical.BoundErrorKind("Overloaded"));
        Assert.Equal(LayoutJobCanonical.MaxErrorKindLength,
            LayoutJobCanonical.BoundErrorKind(new string('e', 500)).Length);
    }

    [Fact]
    public void BackoffGrowsExponentiallyAndIsClamped()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), LayoutJobCanonical.Backoff(1, null));
        Assert.Equal(TimeSpan.FromSeconds(10), LayoutJobCanonical.Backoff(2, null));
        Assert.Equal(TimeSpan.FromSeconds(600), LayoutJobCanonical.Backoff(9, null));
        Assert.Equal(TimeSpan.FromSeconds(5), LayoutJobCanonical.Backoff(0, TimeSpan.FromMilliseconds(10)));
        Assert.Equal(TimeSpan.FromHours(1), LayoutJobCanonical.Backoff(1, TimeSpan.FromDays(2)));
    }

    [Fact]
    public void RetryAfterHintWinsOnlyWhenLonger()
    {
        Assert.Equal(TimeSpan.FromSeconds(30),
            LayoutJobCanonical.Backoff(1, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(10),
            LayoutJobCanonical.Backoff(2, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void EligibilityRequiresBudgetAndElapsedBackoff()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(LayoutJobCanonical.IsEligible(LayoutJobCanonical.Pending, 0, null, now));
        Assert.False(LayoutJobCanonical.IsEligible(LayoutJobCanonical.Succeeded, 0, null, now));
        Assert.False(LayoutJobCanonical.IsEligible(LayoutJobCanonical.Failed,
            LayoutJobCanonical.MaxAttemptsPerImage, null, now));
        Assert.False(LayoutJobCanonical.IsEligible(LayoutJobCanonical.Failed, 1, now.AddSeconds(30), now));
        Assert.True(LayoutJobCanonical.IsEligible(LayoutJobCanonical.Failed, 1, now.AddSeconds(-1), now));
    }

    [Fact]
    public void HasEligibleWorkFollowsImages()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(LayoutJobCanonical.HasEligibleWork(
            [(LayoutJobCanonical.Succeeded, 1, (DateTimeOffset?)null)], now));
        Assert.False(LayoutJobCanonical.HasEligibleWork(
            [(LayoutJobCanonical.Failed, LayoutJobCanonical.MaxAttemptsPerImage, (DateTimeOffset?)null)], now));
        Assert.True(LayoutJobCanonical.HasEligibleWork(
            [(LayoutJobCanonical.Succeeded, 1, (DateTimeOffset?)null),
             (LayoutJobCanonical.Pending, 0, null)], now));
    }

    [Theory]
    [InlineData(0, 0, 90)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 270)]
    public void SourceRotationFollowsTheManifestIndexWhenAligned(int index, int listIndex, int expected)
    {
        var rotations = new[] { 90, 0, 270 };
        Assert.Equal(expected, LayoutJobCanonical.SourceRotationOf(rotations, index));
        Assert.Equal(listIndex, index);
    }

    [Fact]
    public void SourceRotationIsNullWhenNotAttributable()
    {
        Assert.Null(LayoutJobCanonical.SourceRotationOf([], 0));
        Assert.Null(LayoutJobCanonical.SourceRotationOf([90, 0], 5));
        Assert.Null(LayoutJobCanonical.SourceRotationOf([90, 0], -1));
    }
}
