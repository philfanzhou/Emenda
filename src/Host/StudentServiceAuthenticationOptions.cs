using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Ruoyu.Study.Mistake.Host;

public sealed class StudentServiceAuthenticationOptions
{
    public const string Section = "Mistake:Student:ServiceAuthentication";
    public bool Enabled { get; private init; }
    public string Issuer { get; private init; } = "urn:ruoyu:mistake";
    public string Audience { get; private init; } = "urn:ruoyu:student-integration";
    public string Subject { get; private init; } = "ruoyu.mistake";
    public string KeyId { get; private init; } = "";
    internal RSAParameters PrivateKey { get; private init; }

    public static StudentServiceAuthenticationOptions Read(IConfiguration configuration, string studentBaseUrl)
    {
        var section = configuration.GetSection(Section);
        var enabled = Boolean(section, "Enabled");
        var insecure = Boolean(section, "AllowInsecureHttp");
        var issuer = Text(section, "Issuer", "urn:ruoyu:mistake");
        var audience = Text(section, "Audience", "urn:ruoyu:student-integration");
        var subject = Text(section, "Subject", "ruoyu.mistake");
        if (!enabled) return new StudentServiceAuthenticationOptions();
        if (!Uri.TryCreate(studentBaseUrl, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttps && !(insecure && address.Scheme == Uri.UriSchemeHttp)) ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 ||
            address.Fragment.Length != 0 || studentBaseUrl.Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"{Section}:Enabled requires an HTTPS Student base URL; controlled HTTP requires {Section}:AllowInsecureHttp.");
        var kid = section["KeyId"];
        if (kid is null || !Regex.IsMatch(kid, "^[A-Za-z0-9._-]{1,128}$")) throw Invalid("KeyId");
        try
        {
            var path = section["PrivateKeyPath"] ?? "";
            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new CryptographicException();
            var pem = File.ReadAllText(path);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < 2048) throw new CryptographicException();
            return new StudentServiceAuthenticationOptions
            {
                Enabled = true, Issuer = issuer, Audience = audience, Subject = subject,
                KeyId = kid, PrivateKey = rsa.ExportParameters(true)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            throw Invalid("PrivateKeyPath");
        }
    }

    private static bool Boolean(IConfiguration section, string key)
    {
        var value = section[key];
        if (value is null) return false;
        if (bool.TryParse(value, out var result)) return result;
        throw Invalid(key);
    }

    private static string Text(IConfiguration section, string key, string fallback)
    {
        var value = section[key] ?? fallback;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) throw Invalid(key);
        return value;
    }

    private static InvalidOperationException Invalid(string key) => new($"{Section}:{key} is invalid or incomplete.");
}
