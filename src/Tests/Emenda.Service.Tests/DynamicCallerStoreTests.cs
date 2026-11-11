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
    private string StorePath => Path.Combine(_root, "emenda-callers.json");

    private DynamicCallerStore CreateStore() => new(StorePath, NullLogger<DynamicCallerStore>.Instance);

    private async Task BootstrapAsync(string json = "{\"version\":1,\"callers\":[]}")
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(StorePath, json);
    }

    [Fact]
    public async Task RotationPersistsAndRevokesPreviousKeyAcrossStoreInstances()
    {
        await BootstrapAsync();
        var first = CreateStore();
        await first.UpsertAsync("study-platform", "key-1", "admin", "bootstrap");
        (await Registry(first).MatchAsync("key-1")).Should().Be("study-platform");

        var second = CreateStore();
        (await Registry(second).MatchAsync("key-1")).Should().Be("study-platform");
        await second.UpsertAsync("study-platform", "key-2", "admin", "rotated");
        foreach (var registry in new[] { Registry(second), Registry(CreateStore()) })
        {
            (await registry.MatchAsync("key-1")).Should().BeNull();
            (await registry.MatchAsync("key-2")).Should().Be("study-platform");
        }
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("disable")]
    [InlineData("delete")]
    public async Task ExistingVersionOneOverrideNeverResurrectsStaticKeyAfterMutationAndRestart(string operation)
    {
        await BootstrapAsync("""
            {"version":1,"callers":[{"name":"PLATFORM","apiKey":"override-key","enabled":true}]}
            """);
        var store = CreateStore();
        var registry = Registry(store, ("platform", "static-key"), ("unrelated", "unrelated-key"));
        (await registry.MatchAsync("override-key")).Should().Be("platform");
        (await registry.MatchAsync("static-key")).Should().BeNull();

        switch (operation)
        {
            case "rotate":
                await store.UpsertAsync("platform", "rotated-key", "admin", "");
                break;
            case "disable":
                await store.UpdateAsync("platform", false, null);
                break;
            case "delete":
                await store.RemoveAsync("platform");
                break;
        }

        foreach (var effective in new[]
        {
            registry,
            Registry(CreateStore(), ("platform", "static-key"), ("unrelated", "unrelated-key"))
        })
        {
            (await effective.MatchAsync("static-key")).Should().BeNull();
            (await effective.MatchAsync("override-key")).Should().BeNull();
            (await effective.MatchAsync("rotated-key")).Should().Be(operation == "rotate" ? "platform" : null);
            (await effective.MatchAsync("unrelated-key")).Should().Be("unrelated");
            if (operation == "delete") (await effective.ListAsync()).Should().NotContain(entry => entry.Name == "platform");
        }
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(StorePath));
        persisted.RootElement.GetProperty("version").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task DeletedOverrideCannotBeReenabledButExplicitRecreationUsesOnlyNewKey()
    {
        await BootstrapAsync();
        var store = CreateStore();
        await store.UpsertAsync("platform", "dynamic-key", "admin", "");
        await store.RemoveAsync("PLATFORM");
        (await store.RemoveAsync("platform")).Should().BeNull();
        (await store.UpdateAsync("platform", true, null)).Should().BeNull();
        var tombstone = (await store.ListAsync()).Should().ContainSingle().Which;
        tombstone.Deleted.Should().BeTrue();
        tombstone.ApiKey.Should().BeEmpty("deleted credentials must not remain available for reveal");
        var registry = Registry(store, ("platform", "static-key"));
        (await registry.MatchAsync("dynamic-key")).Should().BeNull();
        (await registry.MatchAsync("static-key")).Should().BeNull();

        await store.UpsertAsync("platform", "recreated-key", "admin", "");
        var restarted = Registry(CreateStore(), ("platform", "static-key"));
        (await restarted.MatchAsync("recreated-key")).Should().Be("platform");
        (await restarted.MatchAsync("dynamic-key")).Should().BeNull();
        (await restarted.MatchAsync("static-key")).Should().BeNull();
    }

    [Fact]
    public async Task RotationOfDisabledOverrideDoesNotReactivateEitherCredential()
    {
        await BootstrapAsync();
        var store = CreateStore();
        await store.UpsertAsync("platform", "old-key", "admin", "");
        await store.UpdateAsync("platform", false, null);
        await store.UpsertAsync("platform", "new-key", "admin", "");
        var restarted = Registry(CreateStore(), ("platform", "static-key"));
        (await restarted.MatchAsync("old-key")).Should().BeNull();
        (await restarted.MatchAsync("new-key")).Should().BeNull();
        (await restarted.MatchAsync("static-key")).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    public async Task InvalidNamesAreRejected(string name)
    {
        await BootstrapAsync();
        var act = () => CreateStore().UpsertAsync(name, "key", "admin", "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"callers\":null}")]
    [InlineData("{\"version\":99,\"callers\":[]}")]
    [InlineData("{\"version\":1,\"callers\":[null]}")]
    [InlineData("{\"version\":1,\"callers\":[{\"name\":\"platform\",\"apiKey\":\"\"}]}")]
    [InlineData("{\"version\":1,\"callers\":[{\"name\":\"platform\",\"apiKey\":\"first\"},{\"name\":\"PLATFORM\",\"apiKey\":\"second\"}]}")]
    [InlineData("{\"version\":2,\"callers\":[{\"name\":\"platform\",\"apiKey\":\"\",\"deleted\":true,\"enabled\":true}]}")]
    public async Task InvalidStateBlocksStaticFallbackAndWritesWithoutOverwritingEvidence(string json)
    {
        await BootstrapAsync(json);
        var store = CreateStore();
        var match = () => Registry(store, ("platform", "static-key")).MatchAsync("static-key");
        await match.Should().ThrowAsync<DynamicCallerStoreUnavailableException>();
        var write = () => store.UpsertAsync("platform", "replacement", "admin", "");
        await write.Should().ThrowAsync<DynamicCallerStoreUnavailableException>();
        (await File.ReadAllTextAsync(StorePath)).Should().Be(json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingFileOrEntireVolumeBlocksStaticFallbackAndNeverBootstraps(bool loseVolume)
    {
        await BootstrapAsync();
        var store = CreateStore();
        await store.UpsertAsync("platform", "new-key", "admin", "");
        await store.RemoveAsync("platform");
        if (loseVolume) Directory.Delete(_root, recursive: true);
        else File.Delete(StorePath);

        var restarted = CreateStore();
        var match = () => Registry(restarted, ("platform", "static-key")).MatchAsync("static-key");
        await match.Should().ThrowAsync<DynamicCallerStoreUnavailableException>();
        var create = () => restarted.UpsertAsync("new-caller", "key", "admin", "");
        await create.Should().ThrowAsync<DynamicCallerStoreUnavailableException>();
        File.Exists(StorePath).Should().BeFalse();
    }

    [Fact]
    public async Task ManualRepairRequiresRestartAndDoesNotSilentlyRecoverFailedProcess()
    {
        await BootstrapAsync("broken");
        var failed = CreateStore();
        var load = () => failed.ListAsync();
        await load.Should().ThrowAsync<DynamicCallerStoreUnavailableException>();
        await BootstrapAsync();
        await load.Should().ThrowAsync<DynamicCallerStoreUnavailableException>();
        (await Registry(CreateStore(), ("platform", "static-key")).MatchAsync("static-key"))
            .Should().Be("platform");
    }

    [Fact]
    public async Task DirectFileEditsAreReadOnlyAfterRestart()
    {
        await BootstrapAsync();
        var store = CreateStore();
        await store.UpsertAsync("platform", "dynamic-key", "admin", "");
        var json = await File.ReadAllTextAsync(StorePath);
        await File.WriteAllTextAsync(StorePath, json.Replace("\"enabled\": true", "\"enabled\": false"));
        (await Registry(store, ("platform", "static-key")).MatchAsync("dynamic-key")).Should().Be("platform");
        var restarted = Registry(CreateStore(), ("platform", "static-key"));
        (await restarted.MatchAsync("dynamic-key")).Should().BeNull();
        (await restarted.MatchAsync("static-key")).Should().BeNull();
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("disable")]
    [InlineData("delete")]
    public async Task FailedPersistenceDoesNotPublishUncommittedCredentialChange(string operation)
    {
        await BootstrapAsync();
        var store = CreateStore();
        await store.UpsertAsync("platform", "committed-key", "admin", "");
        Directory.CreateDirectory(StorePath + ".tmp");
        Func<Task> write = operation switch
        {
            "rotate" => async () => { await store.UpsertAsync("platform", "uncommitted-key", "admin", ""); },
            "disable" => async () => { await store.UpdateAsync("platform", false, null); },
            _ => async () => { await store.RemoveAsync("platform"); }
        };
        await write.Should().ThrowAsync<DynamicCallerStoreUnavailableException>();
        foreach (var registry in new[] { Registry(store), Registry(CreateStore()) })
        {
            (await registry.MatchAsync("committed-key")).Should().Be("platform");
            (await registry.MatchAsync("uncommitted-key")).Should().BeNull();
        }
    }

    [Fact]
    public async Task PersistedCredentialsRemainOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        await BootstrapAsync();
        await CreateStore().UpsertAsync("platform", "private-key", "admin", "");
        File.GetUnixFileMode(StorePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task RegistryWithoutStoreServesStaticConfiguration()
    {
        var registry = Registry(null, ("platform", "static-key"));
        (await registry.MatchAsync("static-key")).Should().Be("platform");
        (await registry.MatchAsync("other")).Should().BeNull();
    }

    private static CallerRegistry Registry(DynamicCallerStore? store, params (string Name, string Key)[] callers)
    {
        var options = new ProductCallerAuthenticationOptions();
        options.Callers.AddRange(callers.Select(c => new ProductCallerCredential { Name = c.Name, ApiKey = c.Key }));
        return new CallerRegistry(new OptionsMonitorStub(options), store);
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
