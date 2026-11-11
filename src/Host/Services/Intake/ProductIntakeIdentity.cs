using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Emenda.Service.Intake;

namespace Emenda.Host.Services.Intake;

internal static class ProductIntakeIdentity
{
    internal static string RequestId(string caller, string sourceKey)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "emenda.product-intake.v1");
        Append(hash, caller.ToUpperInvariant());
        Append(hash, sourceKey);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string PayloadHash(ProductIntakeRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        Append(hash, request.StudentId);
        foreach (var image in request.Images)
        {
            Append(hash, ContentType(image.ContentType));
            BinaryPrimitives.WriteInt64BigEndian(length, image.Content.LongLength);
            hash.AppendData(length);
            hash.AppendData(image.Content);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static long LockKey(string requestId) =>
        BinaryPrimitives.ReadInt64BigEndian(Convert.FromHexString(requestId));

    internal static string ContentType(string? value)
    {
        var type = value.AsSpan();
        var separator = type.IndexOf(';');
        if (separator >= 0) type = type[..separator];
        type = type.Trim();
        if (type.Equals("image/png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (type.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        return string.Empty;
    }

    internal static string FileName(int index, string contentType) =>
        $"{index:D2}.{(ContentType(contentType) == "image/png" ? "png" : "jpg")}";

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
