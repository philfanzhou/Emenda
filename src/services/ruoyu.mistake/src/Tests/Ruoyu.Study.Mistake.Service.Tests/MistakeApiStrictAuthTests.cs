using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.Common.Authentication;
using Ruoyu.Study.Mistake.Database;
using Xunit;

// The JWT pipeline factories below mutate process-wide environment variables
// that Program.cs reads during host build (and the subprocess tests inherit
// them), so test classes in this assembly must run sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// IKJEIC acceptance tests for the Mistake inbound JWT authentication window.
/// Tokens are signed with a test signing key and validated by the actual
/// JwtBearer middleware (issuer, signature, lifetime, audience) through a
/// WebApplicationFactory running the real Mistake Program.cs, mirroring the
/// Teacher Portal AdminAudienceAuthorizationTests approach. The PostgreSQL
/// context is swapped for EF Core InMemory.
/// </summary>
public sealed class MistakeApiStrictAuthTests
{
    private const string Issuer = "http://localhost:5002";
    private const string SharedAudience = "QuantumZhou.microservices";
    private const string AdminAppId = "test-admin-app-id-ikjeic";
    private static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("ikjeic-mistake-pipeline-signing-key"));

    private const string ListPath = "/api/mistakes?page=1&size=10&subject=0&grade=0&reviewStatus=0";

    [Theory]
    [InlineData(SharedAudience, false, false, HttpStatusCode.OK)]
    [InlineData(AdminAppId, false, false, HttpStatusCode.OK)]
    [InlineData(SharedAudience, true, false, HttpStatusCode.Unauthorized)]
    [InlineData(AdminAppId, true, false, HttpStatusCode.Unauthorized)]
    [InlineData(SharedAudience, true, true, HttpStatusCode.OK)]
    [InlineData(AdminAppId, true, true, HttpStatusCode.OK)]
    public async Task StrictMode_MetadataIssuerMustBeExplicitlyTrustedByBothSchemesAsync(
        string audience, bool wrongConfiguredIssuer, bool allowMetadataIssuer, HttpStatusCode expected)
    {
        // Authority, discovery issuer, signing key and token stay identical.
        // Only the configured trust list changes; metadata must not widen it.
        using var factory = new MistakeStrictAuthWebApplicationFactory(
            strict: true, adminAudience: AdminAppId,
            configuredIssuer: wrongConfiguredIssuer ? Issuer + "/wrong" : Issuer,
            additionalIssuer: allowMetadataIssuer ? Issuer : null);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest(ListPath, CreateToken(audience)));

        response.StatusCode.Should().Be(expected);
    }

    // ===== strict=true: negative matrix =====

    [Fact]
    public async Task StrictMode_RejectsAnonymousRequestAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ListPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StrictMode_RejectsWrongIssuerAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest(
            ListPath, CreateToken(SharedAudience, issuer: "https://untrusted-issuer.example")));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("some-other-app-id")]
    [InlineData(SharedAudience + "-typo")]
    [InlineData("test-teacher-app-id")]
    public async Task StrictMode_RejectsAnyOtherAudienceAsync(string audience)
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest(ListPath, CreateToken(audience)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StrictMode_RejectsWrongSignatureAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: AdminAppId);
        using var client = factory.CreateClient();
        var wrongKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("ikjeic-mistake-wrong-signing-key-32by"));

        using var response = await client.SendAsync(CreateRequest(
            ListPath, CreateToken(SharedAudience, signingKey: wrongKey)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StrictMode_RejectsExpiredTokenAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest(
            ListPath, CreateToken(SharedAudience, expires: DateTime.UtcNow.AddMinutes(-2))));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StrictMode_RejectsAdminAudienceTokenWhenAdminAudienceIsNotConfiguredAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: null);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest(ListPath, CreateToken(AdminAppId)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ===== strict=true: positive matrix =====

    [Fact]
    public async Task StrictMode_AcceptsSharedAudienceTokenWithoutAnyRoleAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest(ListPath, CreateToken(SharedAudience, role: null)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task StrictMode_AcceptsConfiguredAdminAppIdAudienceTokenAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest(ListPath, CreateToken(AdminAppId)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task StrictMode_KeepsRootEndpointAnonymousAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: true, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Mistake WebAPI host is running.");
    }

    // ===== strict=false (default): anonymous compatibility window =====

    [Fact]
    public async Task CompatibilityWindow_KeepsAnonymousRequestsReachingEndpointsAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: false, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ListPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    [Theory]
    [InlineData("wrong-signature")]
    [InlineData("expired")]
    [InlineData("wrong-audience")]
    public async Task CompatibilityWindow_TreatsFailedTokenValidationAsAnonymousAsync(string failure)
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: false, adminAudience: AdminAppId);
        using var client = factory.CreateClient();
        var token = failure switch
        {
            "wrong-signature" => CreateToken(SharedAudience, signingKey: new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("ikjeic-mistake-wrong-signing-key-32by"))),
            "expired" => CreateToken(SharedAudience, expires: DateTime.UtcNow.AddMinutes(-2)),
            _ => CreateToken("some-other-app-id"),
        };

        using var response = await client.SendAsync(CreateRequest(ListPath, token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task CompatibilityWindow_ValidTokenReachesEndpointAndPopulatesPrincipalAsync()
    {
        using var factory = new MistakeStrictAuthWebApplicationFactory(strict: false, adminAudience: AdminAppId);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest(ListPath, CreateToken(SharedAudience)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");

        // The shared-audience scheme resolves the same token to a principal
        // through the real handler pipeline, i.e. UseAuthentication populates
        // HttpContext.User for valid tokens without any authorization check.
        using var scope = factory.Services.CreateScope();
        var authenticationService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Headers.Authorization = $"Bearer {CreateToken(SharedAudience)}";
        var result = await authenticationService.AuthenticateAsync(context, JwtBearerDefaults.AuthenticationScheme);
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
    }

    private static HttpRequestMessage CreateRequest(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static string CreateToken(
        string audience,
        string? role = null,
        string issuer = Issuer,
        SecurityKey? signingKey = null,
        DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "user-1"),
            new("unique_name", "mistake-user"),
        };
        if (role is not null)
        {
            claims.Add(new Claim("role", role));
        }
        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore: (expires ?? now.AddMinutes(5)).AddMinutes(-10),
            expires: expires ?? now.AddMinutes(5),
            signingCredentials: new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Builds the real Mistake host with the real JWT middleware in Local
    /// mode. Only the OIDC discovery/JWKS transport is replaced by a static
    /// configuration holding the test signing key; issuer, signature,
    /// lifetime and audience validation run through the production pipeline.
    /// The PostgreSQL context is swapped for EF Core InMemory.
    /// </summary>
    internal sealed class MistakeStrictAuthWebApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string[] OwnedEnvironmentKeys =
        [
            "Mistake__ConfigMode",
            "ConnectionStrings__MistakeLocal",
            "Mistake__Local__Oss__InternalEndpoint",
            "Mistake__Local__Oss__InternalSecure",
            "Mistake__Local__Oss__AccessKey",
            "Mistake__Local__Oss__SecretKey",
            "Mistake__Local__Oss__BucketName",
            "Mistake__Local__Oss__PublicBaseUrl",
            "IdentityService__Authority",
            "IdentityService__Issuer",
            "IdentityService__AdditionalValidIssuers__0",
            "IdentityService__Audience",
            "IdentityService__RequireHttpsMetadata",
            "IdentityService__AdminAudience",
            "IdentityService__MistakeApiStrictAuth",
            "IDENTITY_APP_ID_ADMIN",
        ];

        public MistakeStrictAuthWebApplicationFactory(
            bool strict, string? adminAudience, string configuredIssuer = Issuer, string? additionalIssuer = null)
        {
            // Program.cs captures configuration during host build; environment
            // variables have the highest priority and tests run sequentially.
            // The deploy-test fallback key must never satisfy AdminAudience
            // in these tests, so it is removed explicitly.
            Environment.SetEnvironmentVariable("IDENTITY_APP_ID_ADMIN", null);
            Environment.SetEnvironmentVariable("Mistake__ConfigMode", "Local");
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__MistakeLocal",
                "Host=127.0.0.1;Port=5432;Database=mistake_strict_auth_tests;Username=mistake_tester;Password=mistake-strict-auth-tests-only");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__InternalEndpoint", "127.0.0.1:19021");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__InternalSecure", "false");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__AccessKey", "strict-auth-test-access");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__SecretKey", "strict-auth-test-secret");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__BucketName", "mistake-strict-auth-tests");
            Environment.SetEnvironmentVariable("Mistake__Local__Oss__PublicBaseUrl", "http://127.0.0.1/oss");
            Environment.SetEnvironmentVariable("IdentityService__Authority", Issuer);
            Environment.SetEnvironmentVariable("IdentityService__Issuer", configuredIssuer);
            Environment.SetEnvironmentVariable("IdentityService__AdditionalValidIssuers__0", additionalIssuer);
            Environment.SetEnvironmentVariable("IdentityService__Audience", SharedAudience);
            Environment.SetEnvironmentVariable("IdentityService__RequireHttpsMetadata", "false");
            Environment.SetEnvironmentVariable("IdentityService__AdminAudience", adminAudience);
            Environment.SetEnvironmentVariable(
                "IdentityService__MistakeApiStrictAuth", strict ? "true" : null);
        }

        protected override void Dispose(bool disposing)
        {
            // Startup-snapshot keys must never leak into hosts built by later
            // tests (including the subprocess tests, which inherit the
            // process environment).
            foreach (var key in OwnedEnvironmentKeys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var databaseName = $"mistake_strict_auth_{Guid.NewGuid():N}";

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MistakeDbContext>>();
                services.AddDbContext<MistakeDbContext>(options => options.UseInMemoryDatabase(databaseName));

                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, UseStaticSigningKeys);
                services.PostConfigure<JwtBearerOptions>(RuoyuAdminApiAuthorization.SchemeName, UseStaticSigningKeys);
            });
        }

        private static void UseStaticSigningKeys(JwtBearerOptions options)
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(SigningKey);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.Authority = Issuer;
            options.RequireHttpsMetadata = false;
        }
    }
}
