using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Emenda.Host;
using Emenda.HttpContracts;

namespace Emenda.Service.Tests;

public sealed class MistakeHttpContractTests
{
    [Fact]
    public void OperationalReadinessContractNamesOnlyProductDependencies()
    {
        var ops = MistakeHttpContract.Definition.GetProperty("x-ruoyu").GetProperty("ops");
        var ready = ops.GetProperty("GET /health/ready");
        ready.GetProperty("authorization").GetString().Should().Be("anonymous");
        ready.GetProperty("status").EnumerateArray().Select(value => value.GetInt32()).Should().Equal(200, 503);
        ready.GetProperty("dependencies").GetString().Should()
            .Contain("DB/OSS").And.Contain("StructaDoc").And.NotContain("Student").And.NotContain("JWKS");
        ops.GetProperty("GET /health/live").GetProperty("dependencyCalls").GetBoolean().Should().BeFalse();
        ops.GetProperty("GET /contracts/mistake-http-v9.json").GetProperty("dependencyCalls").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("POST", "/api/mistakes/upload")]
    [InlineData("POST", "/api/mistakes")]
    [InlineData("POST", "/api/intake/uploads")]
    [InlineData("POST", "/api/mistakes/00000000-0000-0000-0000-000000000001/reanalyze")]
    [InlineData("GET", "/api/mistakes/00000000-0000-0000-0000-000000000001/analysis")]
    [InlineData("POST", "/api/mistakes/complete-review")]
    [InlineData("GET", "/api/mistakes/reconciliation")]
    [InlineData("GET", "/api/mistakes/intakes/00000000-0000-0000-0000-000000000001/00000000-0000-0000-0000-000000000002")]
    public async Task RemovedIntakeAndAnalysisRoutesAreNotMappedAsync(string method, string path)
    {
        using var factory = new StandaloneWebApplicationFactory(null);
        using var client = factory.Client();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        // 404 (no route) or 405 (path shape matched only by a remaining route of another method).
        ((int)response.StatusCode).Should().BeOneOf(404, 405);
    }

    [Fact]
    public async Task ActualHostInventoryAndAnonymousPublishedBytesMatchPinnedConsumerAsync()
    {
        using var factory = new StandaloneWebApplicationFactory(null);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/contracts/mistake-http-v9.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(MistakeHttpContract.Sha256);
        response.Headers.ETag!.Tag.Should().Be('"' + MistakeHttpContract.Sha256 + '"');

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        bool IsBusiness(string path) => path == "/api/mistakes" || path.StartsWith("/api/mistakes/", StringComparison.Ordinal);
        var actual = endpoints
            .Where(endpoint => IsBusiness(endpoint.RoutePattern.RawText!))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => method.ToLowerInvariant() + " " + Regex.Replace(endpoint.RoutePattern.RawText!, @":\w+", ""))).ToHashSet();
        var specified = MistakeHttpContract.Definition.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(method => method.Name + " " + path.Name)).ToHashSet();
        actual.Should().HaveCount(15).And.BeEquivalentTo(specified);
        foreach (var operation in MistakeHttpContract.Definition.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()))
        {
            var security = operation.Value.GetProperty("security").EnumerateArray().ToList();
            security.Should().ContainSingle();
            security[0].EnumerateObject().Select(value => value.Name).Should().Contain("EmendaApiKey").And.NotContain("Bearer");
        }
        foreach (var other in endpoints.Where(endpoint => !IsBusiness(endpoint.RoutePattern.RawText!)))
        {
            var path = other.RoutePattern.RawText!;
            var namespaces = MistakeHttpContract.Definition.GetProperty("x-ruoyu")
                .GetProperty("otherNamespaces").EnumerateObject().Select(entry => entry.Name);
            namespaces.Should().Contain(owner => BelongsToNamespace(path, owner),
                "every other public route must have an explicit namespace owner: {0}", path);
        }
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/contracts/mistake-http-v9.json");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"different\", W/" + response.Headers.ETag.Tag);
        using var unchanged = await client.SendAsync(conditional);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await unchanged.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        (await client.GetAsync("/contracts/mistake-http-v999.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("mistake-http-v1", "mistake-http-v9")][InlineData("mistake-http-v2", "mistake-http-v9")]
    [InlineData("mistake-http-v3", "mistake-http-v9")][InlineData("mistake-http-v4", "mistake-http-v9")]
    [InlineData("mistake-http-v5", "mistake-http-v9")][InlineData("mistake-http-v6", "mistake-http-v9")]
    [InlineData("emenda-extraction-v2", "emenda-extraction-v3")]
    [InlineData("mistake-http-v8", "mistake-http-v9")]
    [InlineData("mistake-http-v7", "mistake-http-v9")]
    [InlineData("emenda-intake-v1", "emenda-extraction-v3")]
    [InlineData("emenda-extraction-v1", "emenda-extraction-v3")]
    public async Task PreviousPublishedBytesStayAvailableWithTheirOwnHashAndHistoricalMarkerAsync(string version, string successor)
    {
        using var factory = new StandaloneWebApplicationFactory(null);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/contracts/" + version + ".json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var original = typeof(MistakeHttpContract).Assembly.GetManifestResourceStream(version + ".json")!;
        using var bytes = new MemoryStream(); await original.CopyToAsync(bytes);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes.ToArray());
        response.Headers.ETag!.Tag.Should().Be('"' + Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant() + '"');
        response.Headers.GetValues("X-Emenda-Contract-Status").Should().Equal("historical");
        response.Headers.GetValues("Link").Single().Should().Contain(successor + ".json");
        response.Headers.ETag.Tag.Should().NotBe('"' + MistakeHttpContract.Sha256 + '"');
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/contracts/" + version + ".json");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "W/" + response.Headers.ETag.Tag);
        using var cached = await client.SendAsync(conditional);
        cached.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await cached.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/integrations/storage-audit/references/snapshots", false)]
    [InlineData("/api/integrations/storage-audit/references", false)]
    [InlineData("/api/integrations/storage-audit/references-extra", false)]
    [InlineData("/api/integrations/unowned", false)]
    [InlineData("/api/other", false)]
    public void IntegrationNamespaceOwnershipDoesNotHideOtherPublicRoutes(string path, bool expected)
    {
        var namespaces = MistakeHttpContract.Definition.GetProperty("x-ruoyu")
            .GetProperty("otherNamespaces").EnumerateObject().Select(entry => entry.Name);
        namespaces.Any(owner => BelongsToNamespace(path, owner)).Should().Be(expected);
    }

    private static bool BelongsToNamespace(string path, string owner) =>
        path == owner || (owner != "/" && path.StartsWith(owner + "/", StringComparison.Ordinal));

    [Fact]
    public async Task PublisherWorksWithoutAnyBusinessRegistrationAndCancellationCannotCallInfrastructureAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // The appsettings copy in the test output no longer carries a Logging section.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapMistakeContractEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        (await client.GetAsync("/contracts/mistake-http-v9.json")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/contracts/emenda-extraction-v3.json")).Headers.Contains("X-Emenda-Contract-Status").Should().BeFalse();
        using var star = new HttpRequestMessage(HttpMethod.Get, "/contracts/mistake-http-v9.json");
        star.Headers.TryAddWithoutValidation("If-None-Match", "*");
        (await client.SendAsync(star)).StatusCode.Should().Be(HttpStatusCode.NotModified);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Single();
        var http = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = new CancellationToken(true) };
        http.Request.RouteValues["version"] = MistakeContractEndpoints.Version;
        Func<Task> canceled = () => endpoint.RequestDelegate!(http);
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        // No DB or OSS services exist in this host: a dependency access would fail the request.
    }
}
