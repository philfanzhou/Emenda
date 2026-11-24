using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emenda.Host.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Emenda.Service.Tests;

public sealed class PortalStudentAssertionTests
{
    private const string Caller = "portal";
    private const string Issuer = "https://portal.example";
    private const string Audience = "emenda-images";
    private const string Student = "Student-42_ABC";
    private const string UploadPath = "/api/intake/extractions/images";

    [Fact]
    public void Verified_assertion_returns_the_canonical_student_unchanged()
    {
        using var signer = RSA.Create(2048);
        var request = Request(Sign(signer, Claims()));
        request.Request.Headers["X-Student-Id"] = "other-student";

        Assert.True(PortalStudentAssertion.TryResolveStudentId(request, Trust(signer), out var studentId));
        Assert.Equal(Student, studentId);
    }

    [Fact]
    public void Assertion_from_an_untrusted_signer_is_rejected()
    {
        using var trusted = RSA.Create(2048);
        using var other = RSA.Create(2048);
        AssertRejected(Request(Sign(other, Claims())), Trust(trusted));
    }

    [Fact]
    public void A_valid_signature_does_not_allow_a_different_api_key_caller()
    {
        using var signer = RSA.Create(2048);
        // Use the same key and settings to isolate the signed caller binding.
        AssertRejected(Request(Sign(signer, Claims()), "another-caller"), Trust(signer, "another-caller"));
    }

    [Theory]
    [InlineData("iss", "https://another.example")]
    [InlineData("aud", "emenda-images/")]
    [InlineData("purpose", "portal-login")]
    [InlineData("route", "/api/intake/extractions")]
    [InlineData("sub", " Student-42_ABC")]
    [InlineData("sub", "student/42")]
    public void Signed_assertion_with_a_wrong_binding_or_noncanonical_student_is_rejected(string claim, string value)
    {
        using var signer = RSA.Create(2048);
        var claims = Claims();
        claims[claim] = value;
        AssertRejected(Request(Sign(signer, claims)), Trust(signer));
    }

    [Theory]
    [InlineData(-60, -1)]
    [InlineData(-30, 120)]
    [InlineData(60, 90)]
    public void Expired_overlong_or_future_assertion_is_rejected(int issuedOffset, int expiryOffset)
    {
        using var signer = RSA.Create(2048);
        var claims = Claims(issuedOffset, expiryOffset);
        AssertRejected(Request(Sign(signer, claims)), Trust(signer));
    }

    [Fact]
    public void Assertion_requires_the_authentication_context_exact_receive_route_and_single_header()
    {
        using var signer = RSA.Create(2048);
        var token = Sign(signer, Claims());
        var trust = Trust(signer);

        var request = Request(token, caller: null);
        request.Request.Headers["X-Emenda-Api-Key"] = "not-middleware-verified";
        request.Request.Headers["X-Emenda-Caller"] = Caller;
        AssertRejected(request, trust);

        request = Request(token);
        request.Request.Path = "/api/intake/extractions";
        AssertRejected(request, trust);

        request = Request(token);
        request.Request.Headers.Append("X-Emenda-Student-Assertion", token);
        AssertRejected(request, trust);

        request = Request(token);
        request.Request.Headers.Remove("X-Emenda-Student-Assertion");
        request.Request.Headers["X-Student-Id"] = Student;
        AssertRejected(request, trust);

        AssertRejected(Request(token), Trust(signer, "another-caller"));
    }

    private static void AssertRejected(HttpContext request, IConfiguration trust)
    {
        Assert.False(PortalStudentAssertion.TryResolveStudentId(request, trust, out var studentId));
        Assert.Null(studentId);
    }

    private static DefaultHttpContext Request(string token, string? caller = Caller)
    {
        var request = new DefaultHttpContext();
        request.Request.Method = HttpMethods.Post;
        request.Request.Path = UploadPath;
        request.Request.Headers["X-Emenda-Student-Assertion"] = token;
        if (caller is not null) request.Items[ProductCallerAuthenticationMiddleware.CallerNameItemKey] = caller;
        return request;
    }

    private static IConfiguration Trust(RSA signer, string caller = Caller)
    {
        const string prefix = "Emenda:Security:PortalStudentAssertions:0:";
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [prefix + "CallerName"] = caller,
            [prefix + "Issuer"] = Issuer,
            [prefix + "Audience"] = Audience,
            [prefix + "PublicKeyPem"] = signer.ExportSubjectPublicKeyInfoPem()
        }).Build();
    }

    internal static Dictionary<string, object> Claims(int issuedOffset = -10, int expiryOffset = 60)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new()
        {
            ["iss"] = Issuer, ["aud"] = Audience, ["sub"] = Student, ["caller"] = Caller,
            ["purpose"] = "worksheet-images-upload", ["route"] = UploadPath,
            ["iat"] = now + issuedOffset, ["exp"] = now + expiryOffset
        };
    }

    internal static string Sign(RSA signer, Dictionary<string, object> claims)
    {
        var jose = Base64Url.EncodeToString("""{"alg":"RS256","typ":"emenda-student-assertion+jwt"}"""u8);
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));
        var input = $"{jose}.{payload}";
        var signature = signer.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{input}.{Base64Url.EncodeToString(signature)}";
    }
}
