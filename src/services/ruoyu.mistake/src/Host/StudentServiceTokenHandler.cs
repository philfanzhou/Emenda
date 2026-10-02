using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Ruoyu.Study.Mistake.Host;

public sealed class StudentServiceTokenHandler : DelegatingHandler
{
    private readonly StudentServiceAuthenticationOptions _options;
    public StudentServiceTokenHandler(StudentServiceAuthenticationOptions options) => _options = options;

    private static bool IsSnapshotRoute(HttpMethod method, Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/');
        return (method == HttpMethod.Put || method == HttpMethod.Get) && uri.Query.Length == 0
            && segments.Length == 8 && segments[1] == "api" && segments[2] == "integrations"
            && segments[3] == "mistake" && segments[4] == "uploads" && segments[6] == "snapshots"
            && Guid.TryParseExact(segments[5], "D", out var source) && source != Guid.Empty
            && Guid.TryParseExact(segments[7], "D", out var revision) && revision != Guid.Empty;
    }

    private static bool IsReceiptRoute(HttpMethod method, Uri uri)
    {
        var segments=uri.AbsolutePath.Split('/');
        return method==HttpMethod.Get && uri.Query.Length==0 && segments.Length==7
            && segments[1]=="api" && segments[2]=="integrations" && segments[3]=="mistake"
            && segments[4]=="returns" && segments[6]=="receipt" && segments[5].Length is >0 and <=128
            && segments[5].All(c=>char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled) return base.SendAsync(request, cancellationToken);
        var uri = request.RequestUri ?? throw new InvalidOperationException("Student service request URI is missing.");
        var path = uri.AbsolutePath;
        // Ownership resolution uses a dedicated route, leaving Portal account queries unchanged.
        if (path.StartsWith("/api/accounts/", StringComparison.Ordinal) && path.EndsWith("/students", StringComparison.Ordinal))
        {
            path = "/api/integrations/mistake/accounts/" + path["/api/accounts/".Length..];
            request.RequestUri = new UriBuilder(uri) { Path = path }.Uri;
        }
        var scope = path switch
        {
            "/api/uploads/processing/scan" => "uploads.scan",
            _ when IsReceiptRoute(request.Method, uri) => "uploads.return",
            _ when IsSnapshotRoute(request.Method, uri) => "uploads.snapshot",
            _ when path.StartsWith("/api/integrations/mistake/accounts/", StringComparison.Ordinal) => "accounts.read",
            _ when path.StartsWith("/api/students/", StringComparison.Ordinal) && path.EndsWith("/uploads/returned", StringComparison.Ordinal) => "uploads.return",
            _ when path.StartsWith("/api/uploads/returned/", StringComparison.Ordinal) => "uploads.return",
            _ when path.StartsWith("/api/uploads/", StringComparison.Ordinal) && path.EndsWith("/remove-processed", StringComparison.Ordinal) => "uploads.remove",
            _ => throw new InvalidOperationException("Student service request route is unsupported.")
        };
        try
        {
            // Each request owns its RSA instance; concurrent requests share no mutable signing state.
            using var rsa = RSA.Create();
            rsa.ImportParameters(_options.PrivateKey);
            var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var token = new JwtSecurityToken(_options.Issuer, _options.Audience,
                [new Claim("sub", _options.Subject), new Claim("scope", scope),
                    new Claim("iat", issued.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
                    new Claim("jti", Guid.NewGuid().ToString("N"))],
                DateTimeOffset.FromUnixTimeSeconds(issued).UtcDateTime,
                DateTimeOffset.FromUnixTimeSeconds(issued + 60).UtcDateTime,
                new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = _options.KeyId, CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } }, SecurityAlgorithms.RsaSha256));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Student service authentication signing failed.");
        }
        return base.SendAsync(request, cancellationToken);
    }
}
