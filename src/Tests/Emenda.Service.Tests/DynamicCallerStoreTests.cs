using System.Text.Json;
using Emenda.Host.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emenda.Service.Tests;

/// <summary>
/// The dynamic caller store: file-backed CRUD, atomic persistence, name/key
/// validation and the static/dynamic merge of <see cref="CallerRegistry"/>.
/// </summary>
public sealed class DynamicCallerStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"emenda-callers-{Guid.NewGuid():N}");
    private static string StorePath(string root) => Path.Combine(root, "emenda-callers.json");

    private DynamicCallerStore CreateStore(string? path = null) => new(
        path ?? StorePath(_root), NullLogger<DynamicCallerStore>.Instance);

    [Fact]
    public async Task UpsertPersistsAndReloadsAcrossStoreInstances()
    {
        var first = CreateStore();
        var created = await first.UpsertAsync("study-platform", "key-1", "admin", "bootstrap");
        created.Enabled.Should().BeTrue();
        created.CreatedBy.Should().Be("admin");

        var second = CreateStore();
        var list = await second.ListAsync();
        list.Should().ContainSingle().Which.ApiKey.Should().Be("key-1");

        var rotated = await second.UpsertAsync("study-platform", "key-2", "admin", "rotated");
        rotated.ApiKey.Should().Be("key-2");
        rotated.CreatedAtUtc.Should().NotBeEmpty();
        (await new DynamicCallerStore(StorePath(_root), NullLogger<DynamicCallerStore>.Instance).ListAsync())
            .Should().ContainSingle().Which.ApiKey.Should().Be("key-2");
    }

    [Fact]
    public async Task UpdateAndRemoveRoundTrip()
    {
        var store = CreateStore();
        await store.UpsertAsync("a", "key-a", "admin", "");
        (await store.UpdateAsync("A", enabled: false, note: "paused")).Should().NotBeNull();
        (await store.ListAsync()).Should().ContainSingle().Which.Enabled.Should().BeFalse();

        (await store.RemoveAsync("a")).Should().NotBeNull();
        (await store.RemoveAsync("a")).Should().BeNull();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    public async Task InvalidNamesAreRejected(string name)
    {
        var store = CreateStore();
        var act = () => store.UpsertAsync(name, "key", "admin", "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task InvalidFileFailsClosed()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(StorePath(_root), "{ not json");
        var store = CreateStore();
        (await store.ListAsync()).Should().BeEmpty();
        // A subsequent valid write recovers the store.
        await store.UpsertAsync("b", "key-b", "admin", "");
        (await store.ListAsync()).Should().ContainSingle().Which.Name.Should().Be("b");
    }

    [Fact]
    public async Task WrittenDocumentIsVersionedJson()
    {
        var store = CreateStore();
        await store.UpsertAsync("c", "key-c", "admin", "note-c");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(StorePath(_root)));
        document.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("callers")[0]
            .GetProperty("name").GetString().Should().Be("c");
    }

    // ---- CallerRegistry merge ----

    private static ProductCallerAuthenticationOptions Options(params (string Name, string Key)[] callers)
    {
        var options = new ProductCallerAuthenticationOptions();
        options.Callers.AddRange(callers.Select(c => new ProductCallerCredential { Name = c.Name, ApiKey = c.Key }));
        return options;
    }

    private static CallerRegistry Registry(
        (string Name, string Key)[] staticCallers,
        DynamicCallerStore? store) =>
        new(new OptionsMonitorStub(Options(staticCallers)), store);

    [Fact]
    public async Task RegistryMergesByNameWithDynamicPriority()
    {
        var store = CreateStore();
        await store.UpsertAsync("platform", "dynamic-key", "admin", "");
        await store.UpsertAsync("extra", "extra-key", "admin", "");
        var registry = Registry(new[] { ("platform", "static-key") }, store);

        var entries = await registry.ListAsync();
        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Name == "platform" && e.Source == CallerEntrySource.Dynamic && e.ApiKey == "dynamic-key");
        entries.Should().Contain(e => e.Name == "extra" && e.Source == CallerEntrySource.Dynamic);
    }

    [Fact]
    public async Task RegistryMatchesMergedKeysAndHonoursDisabled()
    {
        var store = CreateStore();
        await store.UpsertAsync("platform", "dynamic-key", "admin", "");
        var registry = Registry(new[] { ("platform", "static-key"), ("legacy", "legacy-key") }, store);

        (await registry.MatchAsync("dynamic-key")).Should().Be("platform");
        (await registry.MatchAsync("legacy-key")).Should().Be("legacy");
        (await registry.MatchAsync("static-key")).Should().BeNull("the dynamic entry replaced the static credential");

        await store.UpdateAsync("platform", enabled: false, note: null);
        (await registry.MatchAsync("dynamic-key")).Should().BeNull("disabled entries never match");
    }

    [Fact]
    public async Task RegistryWithoutStoreServesStaticConfiguration()
    {
        var registry = Registry(new[] { ("platform", "static-key") }, null);
        registry.HasDynamicStore.Should().BeFalse();
        (await registry.MatchAsync("static-key")).Should().Be("platform");
        (await registry.MatchAsync("other")).Should().BeNull();
    }

    private sealed class OptionsMonitorStub(ProductCallerAuthenticationOptions current)
        : IOptionsMonitor<ProductCallerAuthenticationOptions>
    {
        public ProductCallerAuthenticationOptions CurrentValue => current;
        public ProductCallerAuthenticationOptions Get(string? name) => current;
        public IDisposable? OnChange(Action<ProductCallerAuthenticationOptions, string?> listener) => null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
