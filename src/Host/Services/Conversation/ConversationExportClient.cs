using System.Net.Http.Json;

namespace Emenda.Host.Services.Conversation;

public interface IConversationExportReader
{
    Task<ConversationCollectionResponse> ReadAsync(Guid operation, string owner, bool checkOnly, CancellationToken ct);
}

public sealed class ConversationExportClient(IHttpClientFactory clients, ConversationCollectionOptions options) : IConversationExportReader
{
    public async Task<ConversationCollectionResponse> ReadAsync(Guid operation, string owner, bool checkOnly, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.TutorUrl.TrimEnd('/') + "/"),
            $"api/v1/tutoring/question-operations/{operation:D}" + (checkOnly ? "?checkOnly=true" : "")));
        request.Headers.Add("X-Agent-Owner", owner); request.Headers.Add("X-Question-Collection-Key", options.CollectionKey);
        using var client = clients.CreateClient("ConversationCollection");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new InvalidDataException("Invalid collection response.");
        await response.Content.LoadIntoBufferAsync(checkOnly ? 64 * 1024 : 21 * 1024 * 1024, ct);
        var result = await response.Content.ReadFromJsonAsync<ConversationCollectionResponse>(ConversationExport.Json, ct) ?? throw new InvalidDataException("Missing collection response.");
        if (result.Status == "completed" && (result.Fingerprint is not { Length: 64 } || !result.Fingerprint.All(char.IsAsciiHexDigit) || !checkOnly && result.Result == null))
            throw new InvalidDataException("Missing immutable export identity.");
        return result;
    }
}

public sealed class ConversationSourceChangedException : Exception { }
