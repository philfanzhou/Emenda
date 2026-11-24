using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Emenda.Host.Security;

/// <summary>Resolves a Portal-attested student after product caller authentication, for the images-only upload only.</summary>
public static class PortalStudentAssertion
{
    public const string UploadPath = "/api/intake/extractions/images";
    public const string HeaderName = "X-Emenda-Student-Assertion";
    public const string ConfigurationSection = "Emenda:Security:PortalStudentAssertions";
    private const string Purpose = "worksheet-images-upload";
    private const string TokenType = "emenda-student-assertion+jwt";
    private const int MaxLifetimeSeconds = 120;
    private const int MaxTokenLength = 4096;
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    public static bool TryResolveStudentId(HttpContext http, IConfiguration configuration,
        [NotNullWhen(true)] out string? studentId)
    {
        studentId = null;
        if (http.Request.Method != HttpMethods.Post || http.Request.Path.Value != UploadPath ||
            !http.Items.TryGetValue(ProductCallerAuthenticationMiddleware.CallerNameItemKey, out var item) ||
            item is not string caller || string.IsNullOrWhiteSpace(caller))
            return false;

        var values = http.Request.Headers[HeaderName];
        if (values.Count != 1 || values[0] is not { Length: > 0 and <= MaxTokenLength } token)
            return false;
        foreach (var c in token)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) return false;

        var firstDot = token.IndexOf('.');
        var lastDot = token.LastIndexOf('.');
        if (firstDot <= 0 || lastDot <= firstDot + 1 || lastDot == token.Length - 1 ||
            token.AsSpan(firstDot + 1, lastDot - firstDot - 1).Contains('.'))
            return false;

        // Select trust only from the API-key middleware's caller, not from a JWT claim or header.
        IConfigurationSection? trust = null;
        foreach (var entry in configuration.GetSection(ConfigurationSection).GetChildren())
        {
            if (entry["CallerName"] != caller) continue;
            if (trust is not null) return false;
            trust = entry;
        }

        if (trust is null) return false;
        var issuer = trust["Issuer"];
        var audience = trust["Audience"];
        var pem = trust["PublicKeyPem"].AsSpan().Trim();
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) ||
            !PemEncoding.TryFind(pem, out var key) || !pem[key.Label].SequenceEqual("PUBLIC KEY") ||
            key.Location.GetOffsetAndLength(pem.Length) != (0, pem.Length))
            return false;

        try
        {
            using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(token.AsSpan(0, firstDot)), StrictJson);
            var jose = header.RootElement;
            if (jose.ValueKind != JsonValueKind.Object || !Matches(jose, "alg", "RS256") || !Matches(jose, "typ", TokenType))
                return false;
            foreach (var property in jose.EnumerateObject())
                if (!property.NameEquals("alg") && !property.NameEquals("typ")) return false;

            using var rsa = RSA.Create();
            var publicKey = new byte[key.DecodedDataLength];
            if (!Convert.TryFromBase64Chars(pem[key.Base64Data], publicKey, out var keyLength) || keyLength != publicKey.Length)
                return false;
            rsa.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length || rsa.KeySize < 2048) return false;

            Span<byte> signingInput = stackalloc byte[lastDot];
            Encoding.ASCII.GetBytes(token.AsSpan(0, lastDot), signingInput);
            Span<byte> signature = stackalloc byte[token.Length - lastDot - 1];
            var signatureLength = Base64Url.DecodeFromChars(token.AsSpan(lastDot + 1), signature);
            if (!rsa.VerifyData(signingInput, signature[..signatureLength], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return false;

            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(token.AsSpan(firstDot + 1, lastDot - firstDot - 1)), StrictJson);
            var claims = payload.RootElement;
            if (claims.ValueKind != JsonValueKind.Object || !Matches(claims, "iss", issuer) || !Matches(claims, "aud", audience) ||
                !Matches(claims, "caller", caller) || !Matches(claims, "purpose", Purpose) || !Matches(claims, "route", UploadPath))
                return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!claims.TryGetProperty("iat", out var issuedAt) || !TryNumericDate(issuedAt, out var iat) || iat > now ||
                !claims.TryGetProperty("exp", out var expiresAt) || !TryNumericDate(expiresAt, out var exp) || exp <= now ||
                exp - iat > MaxLifetimeSeconds ||
                (claims.TryGetProperty("nbf", out var notBefore) && (!TryNumericDate(notBefore, out var nbf) || nbf > now)))
                return false;

            if (!claims.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String ||
                subject.GetString() is not { Length: >= 1 and <= 36 } id || !char.IsAsciiLetterOrDigit(id[0]))
                return false;
            foreach (var c in id)
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) return false;

            studentId = id;
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Matches(JsonElement obj, string name, string value) =>
        obj.TryGetProperty(name, out var claim) && claim.ValueKind == JsonValueKind.String && claim.ValueEquals(value);

    private static bool TryNumericDate(JsonElement element, out long seconds)
    {
        seconds = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out seconds) && seconds >= 0;
    }
}
