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
using Ruoyu.Study.Mistake.Host;
using Ruoyu.Study.Mistake.HttpContracts;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class MistakeHttpContractTests
{
    [Fact]
    public void OperationalReadinessContractIncludesConditionalStrictIssuerDependency()
    {
        var ops = MistakeHttpContract.Definition.GetProperty("x-ruoyu").GetProperty("ops");
        var ready = ops.GetProperty("GET /health/ready");
        ready.GetProperty("authorization").GetString().Should().Be("anonymous");
        ready.GetProperty("status").EnumerateArray().Select(value => value.GetInt32()).Should().Equal(200, 503);
        ready.GetProperty("dependencies").GetString().Should()
            .Contain("strict-enabled issuer").And.Contain("exact discovery issuer").And.Contain("same-origin nonempty JWKS");
        ops.GetProperty("GET /health/live").GetProperty("dependencyCalls").GetBoolean().Should().BeFalse();
        ops.GetProperty("GET /contracts/mistake-http-v2.json").GetProperty("dependencyCalls").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("mistake")]
    [InlineData("homework")]
    [InlineData("custom-preserved-metadata")]
    public void SourceImageMetadataIsRequiredButDoesNotPretendToBeAClosedEligibilityEnum(string type)
    {
        using var image = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            path = "uploads/synthetic.png", type, rotation = 0, length = 64, sha256 = new string('a', 64)
        }));
        var schema = MistakeHttpContract.Definition.GetProperty("components").GetProperty("schemas").GetProperty("ManifestImage");
        MistakeHttpContract.ValidateSchema(image.RootElement, schema);
        using var missing = JsonDocument.Parse("{}");
        Action absent = () => MistakeHttpContract.ValidateSchema(missing.RootElement, schema);
        absent.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualHostInventoryAndAnonymousPublishedBytesMatchPinnedConsumerAsync(bool strict)
    {
        using var factory = new MistakeApiStrictAuthTests.MistakeStrictAuthWebApplicationFactory(strict, "test-admin-app-id-ikjeic");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/contracts/mistake-http-v2.json");
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
        actual.Should().HaveCount(23).And.BeEquivalentTo(specified);
        foreach (var path in MistakeHttpContract.Definition.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.StartsWith("/api/mistakes/intakes/", StringComparison.Ordinal)))
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var security = operation.Value.GetProperty("security").EnumerateArray().ToList();
                security.Should().HaveCount(1);
                security[0].EnumerateObject().Select(value => value.Name).Should().Equal("Bearer");
                operation.Value.GetProperty("x-ruoyu").GetProperty("authorization").GetProperty("compatibility")
                    .GetString().Should().Be("always authenticated Admin subject; no anonymous compatibility window");
            }
        }
        foreach (var other in endpoints.Where(endpoint => !IsBusiness(endpoint.RoutePattern.RawText!)))
        {
            var path = other.RoutePattern.RawText!;
            var namespaces = MistakeHttpContract.Definition.GetProperty("x-ruoyu")
                .GetProperty("otherNamespaces").EnumerateObject().Select(entry => entry.Name);
            namespaces.Should().Contain(owner => BelongsToNamespace(path, owner),
                "every other public route must have an explicit namespace owner: {0}", path);
        }
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/contracts/mistake-http-v2.json");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"different\", W/" + response.Headers.ETag.Tag);
        using var unchanged = await client.SendAsync(conditional);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await unchanged.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        (await client.GetAsync("/contracts/mistake-http-v999.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PreviousPublishedBytesStayAvailableWithTheirOwnHashAndHistoricalMarkerAsync()
    {
        using var factory = new MistakeApiStrictAuthTests.MistakeStrictAuthWebApplicationFactory(true, "test-admin-app-id-ikjeic");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/contracts/mistake-http-v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var original = typeof(MistakeHttpContract).Assembly.GetManifestResourceStream("mistake-http-v1.json")!;
        using var bytes = new MemoryStream(); await original.CopyToAsync(bytes);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes.ToArray());
        response.Headers.ETag!.Tag.Should().Be('"' + Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant() + '"');
        response.Headers.GetValues("X-Ruoyu-Contract-Status").Should().Equal("historical");
        response.Headers.GetValues("Link").Single().Should().Contain("mistake-http-v2.json");
        response.Headers.ETag.Tag.Should().NotBe('"' + MistakeHttpContract.Sha256 + '"');
    }

    [Theory]
    [InlineData("/api/integrations/storage-audit/references/snapshots", true)]
    [InlineData("/api/integrations/storage-audit/references", true)]
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
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapMistakeContractEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        (await client.GetAsync("/contracts/mistake-http-v2.json")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var star = new HttpRequestMessage(HttpMethod.Get, "/contracts/mistake-http-v2.json");
        star.Headers.TryAddWithoutValidation("If-None-Match", "*");
        (await client.SendAsync(star)).StatusCode.Should().Be(HttpStatusCode.NotModified);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Single();
        var http = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = new CancellationToken(true) };
        http.Request.RouteValues["version"] = MistakeContractEndpoints.Version;
        Func<Task> canceled = () => endpoint.RequestDelegate!(http);
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        // No DB, OSS or Student services exist in this host: a dependency access would fail the request.
    }
}
