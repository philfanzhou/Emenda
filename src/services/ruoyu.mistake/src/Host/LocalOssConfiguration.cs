using Microsoft.Extensions.Configuration;
using Ruoyu.Study.Common.Oss;

namespace Ruoyu.Study.Mistake.Host;

internal static class LocalOssConfiguration
{
    private const string Prefix = "Mistake:Local:Oss:";

    public static OssOptions Read(IConfiguration configuration)
    {
        var endpoint = Required(configuration, "InternalEndpoint");
        var secureValue = Required(configuration, "InternalSecure");
        if (!bool.TryParse(secureValue, out var secure))
            throw Invalid("InternalSecure", "must be true or false");

        var accessKey = Required(configuration, "AccessKey");
        var secretKey = Required(configuration, "SecretKey");
        var bucket = Required(configuration, "BucketName");
        var publicBaseUrl = Required(configuration, "PublicBaseUrl");

        if (accessKey == "mock_access_key" || secretKey == "mock_secret_key")
            throw Invalid("AccessKey/SecretKey", "must not use mock credentials");

        if (!Uri.TryCreate($"http://{endpoint}", UriKind.Absolute, out var internalUri) ||
            internalUri.Host.Length == 0 || internalUri.UserInfo.Length != 0 ||
            internalUri.AbsolutePath != "/" || internalUri.Query.Length != 0 ||
            internalUri.Fragment.Length != 0 || endpoint.Contains("://", StringComparison.Ordinal) ||
            endpoint.Any(char.IsWhiteSpace))
            throw Invalid("InternalEndpoint", "must be a host or host:port without a scheme or path");

        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicUri) ||
            (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps) ||
            publicUri.Host.Length == 0 || publicUri.UserInfo.Length != 0 ||
            publicUri.Query.Length != 0 || publicUri.Fragment.Length != 0 ||
            publicBaseUrl.Any(char.IsWhiteSpace))
            throw Invalid("PublicBaseUrl", "must be an absolute HTTP(S) URL without credentials, query or fragment");

        return new OssOptions
        {
            InternalEndpoint = endpoint,
            InternalSecure = secure,
            AccessKey = accessKey,
            SecretKey = secretKey,
            BucketName = bucket,
            PublicBaseUrl = publicBaseUrl
        };
    }

    private static string Required(IConfiguration configuration, string name)
    {
        var value = configuration[Prefix + name];
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(name, "is required");
        return value;
    }

    private static InvalidOperationException Invalid(string name, string reason) =>
        new($"{Prefix}{name} {reason}.");
}
