using System.Net.Http.Json;
using System.Text.Json;

namespace Emenda.Host.Services.Conversation;

public interface IConversationAuthorization
{
    Task<bool> AllowedAsync(string actorId, string studentId, string ownerId, CancellationToken ct);
}

/// <summary>Only explicit denial revokes access. Transport and protocol errors remain retryable.</summary>
public sealed class ConversationAuthorizationClient(IHttpClientFactory clients, ConversationCollectionOptions options) : IConversationAuthorization
{
    public async Task<bool> AllowedAsync(string actorId, string studentId, string ownerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.AuthorizationBaseUrl), "/api/ai-tutor/collection/authorize"));
        request.Headers.Add("X-Question-Collection-Key", options.CollectionKey);
        request.Content = JsonContent.Create(new { actorId, studentId, ownerId });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = clients.CreateClient("ConversationCollection");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new HttpRequestException("Collection authorization is unavailable.");
        try
        {
            await response.Content.LoadIntoBufferAsync(4096, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 1
                || !document.RootElement.TryGetProperty("authorized", out var authorized)
                || authorized.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new HttpRequestException("Collection authorization is unavailable.");
            return authorized.GetBoolean();
        }
        catch (JsonException ex) { throw new HttpRequestException("Collection authorization is unavailable.", ex); }
    }
}
