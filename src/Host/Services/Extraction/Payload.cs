using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emenda.Service.Intake;

namespace Emenda.Host.Services.Extraction;

internal static class Payload
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new ExtractionException("invalid_retained_state", 503);
    internal static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string HashJson<T>(T value) => HashBytes(JsonSerializer.SerializeToUtf8Bytes(value, Json));
    internal static Guid Identity(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    internal static long LockKey(string value) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0);
}
