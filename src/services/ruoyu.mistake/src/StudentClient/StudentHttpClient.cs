using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.Mistake.StudentClient;

// Mistake-owned outbound Student client (IKJFF5). This library decouples the
// Mistake service from the Portal-facing shared MistakeBff.HttpClients
// library. It exposes exactly the four endpoints Mistake consumes and ports
// their wire contracts, JSON options, logging and failure classification
// verbatim from the former shared implementation; the shared library keeps
// serving the three Portal BFF layers unchanged.

// ===== DTOs matching student service HTTP response payload (the "data" portion) =====

public class ImageEntryDto
{
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = "mistake";
}

public class UploadRecordDto
{
    public Guid ContentRevision { get; set; }
    public string Id { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public int Status { get; set; }
    public List<ImageEntryDto> ImageEntries { get; set; } = new();
    public List<int> ImageRotations { get; set; } = new();
    public string Comments { get; set; } = string.Empty;
    public string ReturnReason { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class ProcessingUploadScanPage
{
    public List<UploadRecordDto>? Items { get; set; }
    public bool HasMore { get; set; }
    public string? NextCursor { get; set; }
}

public enum ProcessingUploadScanFailure
{
    HttpStatus,
    Business,
    Transport,
    Timeout,
    InvalidResponse
}

public sealed class ProcessingUploadScanException : Exception
{
    public ProcessingUploadScanFailure Failure { get; }

    public ProcessingUploadScanException(ProcessingUploadScanFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;
}

public class CreateReturnedRecordRequest
{
    public List<ImageEntryDto> ImageEntries { get; set; } = new();
    public string? Comments { get; set; }
    public string? ReturnReason { get; set; }

    /// <summary>
    /// Optional idempotency key (IKJEY4): same key + same payload replays the
    /// stored record; same key + different payload is a payload conflict.
    /// Null keeps the legacy behavior.
    /// </summary>
    public string? OperationId { get; set; }
}

/// <summary>
/// Outcome classification of <see cref="IStudentHttpClient.CreateReturnedRecordAsync"/>
/// (IKJEY4): Succeeded (record created or replayed), PayloadConflict (the
/// operationId was already used with a different payload; no side effects on
/// this attempt) or Unknown (transport failure, timeout or unreadable
/// response — whether Student created the record is unknown and must be
/// read back before retrying with the same operationId).
/// </summary>
public enum CreateReturnedRecordStatus
{
    Succeeded = 0,
    PayloadConflict = 1,
    Unknown = 2
}

public class CreateReturnedRecordOutcome
{
    public CreateReturnedRecordStatus Status { get; set; } = CreateReturnedRecordStatus.Unknown;
    public bool Replayed { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class RemoveProcessedImagesResult
{
    public bool Success { get; set; }
    public bool RecordDeleted { get; set; }
    public int RemainingImageCount { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Passthrough of the Student failure classification (IKJEY6):
    /// "none", "unknown_record" (record does not exist and no removal
    /// tombstone proves a prior completion — investigate, do not retry) or
    /// empty when Student did not send one.
    /// </summary>
    public string ErrorKind { get; set; } = string.Empty;
}

public class StudentInfoDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Grade { get; set; }
    public bool IsDefault { get; set; }
    public List<string> IdentityAccountIds { get; set; } = new();
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public class StudentsByAccountResult
{
    public List<StudentInfoDto> Students { get; set; } = new();
}

// ===== Interface =====

public interface IStudentHttpClient
{
    Task<CreateReturnedRecordOutcome> CreateReturnedRecordAsync(string studentId, List<ImageEntryDto> imageEntries, string? comments, string? returnReason, string? operationId, CancellationToken ct = default);
    Task<ProcessingUploadScanPage> ScanProcessingUploadRecordsAsync(string? cursor, int pageSize, CancellationToken ct = default);
    Task<RemoveProcessedImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken ct = default);

    /// <summary>
    /// Strict ownership resolution variant (Mistake IKJEPZ): unlike the
    /// Portal-facing lenient method it never swallows failures into an
    /// ambiguous empty result. Any transport failure, non-success envelope
    /// or timeout throws, so the caller can fail closed instead of
    /// misreading "call failed" as "account has no students".
    /// </summary>
    Task<StudentsByAccountResult> GetStudentsByIdentityAccountIdStrictAsync(string accountId, CancellationToken ct = default);
}

// ===== Implementation =====

public class StudentHttpClient : IStudentHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<StudentHttpClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public StudentHttpClient(HttpClient httpClient, ILogger<StudentHttpClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<CreateReturnedRecordOutcome> CreateReturnedRecordAsync(string studentId, List<ImageEntryDto> imageEntries, string? comments, string? returnReason, string? operationId, CancellationToken ct = default)
    {
        var body = new CreateReturnedRecordRequest
        {
            ImageEntries = imageEntries,
            Comments = comments,
            ReturnReason = returnReason,
            OperationId = operationId
        };
        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"/api/students/{studentId}/uploads/returned", body, JsonOptions, ct);
            if ((int)response.StatusCode == 409)
            {
                const string fallbackMessage = "CreateReturnedRecord payload conflict";
                string conflictMessage;
                try
                {
                    var conflictEnvelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<object>>(JsonOptions, ct);
                    conflictMessage = conflictEnvelope?.Message ?? fallbackMessage;
                }
                catch (JsonException)
                {
                    // HTTP 409 proves a conflict even when its explanation is unreadable.
                    // Do not log the body or parsing exception, which may contain upstream data.
                    conflictMessage = fallbackMessage;
                }
                _logger.LogWarning("CreateReturnedRecord payload conflict: {Message}, Status: {Status}", conflictMessage, response.StatusCode);
                return new CreateReturnedRecordOutcome
                {
                    Status = CreateReturnedRecordStatus.PayloadConflict,
                    Message = conflictMessage
                };
            }

            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<CreateReturnedRecordOutcomeData>>(JsonOptions, ct);
            if (response.IsSuccessStatusCode && envelope is not null && envelope.Success
                && envelope.Data is not null && envelope.Data.Success)
            {
                return new CreateReturnedRecordOutcome
                {
                    Status = CreateReturnedRecordStatus.Succeeded,
                    Replayed = envelope.Data.Replayed,
                    Message = envelope.Message ?? string.Empty
                };
            }

            // Non-success response with a known status (e.g. 400 validation)
            // or a business-level failure: the record was not confirmed
            // created, so the result stays unknown to the caller.
            var msg = envelope?.Message ?? "CreateReturnedRecord failed";
            _logger.LogWarning("CreateReturnedRecord failed: {Message}, Status: {Status}", msg, response.StatusCode);
            return new CreateReturnedRecordOutcome
            {
                Status = CreateReturnedRecordStatus.Unknown,
                Message = msg
            };
        }
        catch (JsonException)
        {
            const string message = "CreateReturnedRecord response is unreadable; whether the record was created is unknown";
            _logger.LogWarning("{Message}", message);
            return new CreateReturnedRecordOutcome
            {
                Status = CreateReturnedRecordStatus.Unknown,
                Message = message
            };
        }
        catch (HttpRequestException ex)
        {
            // Not swallowed: the transport failure is surfaced as an explicit
            // Unknown outcome whose message carries the original exception.
            _logger.LogError(ex, "CreateReturnedRecord HTTP request failed; whether the record was created is unknown");
            return new CreateReturnedRecordOutcome
            {
                Status = CreateReturnedRecordStatus.Unknown,
                Message = ex.Message
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "CreateReturnedRecord HTTP request timed out; whether the record was created is unknown");
            return new CreateReturnedRecordOutcome
            {
                Status = CreateReturnedRecordStatus.Unknown,
                Message = "CreateReturnedRecord timed out"
            };
        }
    }

    public async Task<ProcessingUploadScanPage> ScanProcessingUploadRecordsAsync(string? cursor, int pageSize, CancellationToken ct = default)
    {
        var url = $"/api/uploads/processing/scan?pageSize={pageSize}";
        if (cursor is not null)
            url += "&cursor=" + Uri.EscapeDataString(cursor);

        try
        {
            using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProcessingUploadScanException(ProcessingUploadScanFailure.HttpStatus,
                    $"Student scan returned HTTP {(int)response.StatusCode}.");

            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<ProcessingUploadScanPage>>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (envelope is null)
                throw new ProcessingUploadScanException(ProcessingUploadScanFailure.InvalidResponse, "Student scan response is empty.");
            if (!envelope.Success)
                throw new ProcessingUploadScanException(ProcessingUploadScanFailure.Business, "Student scan reported failure.");

            var page = envelope.Data;
            if (page?.Items is null || page.HasMore != !string.IsNullOrWhiteSpace(page.NextCursor) ||
                page.Items.Count > pageSize || (page.HasMore && page.Items.Count == 0) ||
                page.Items.Any(item => item is null ||
                    !Guid.TryParse(item.Id, out _) || string.IsNullOrWhiteSpace(item.StudentId) ||
                    item.ImageEntries is null || item.ImageEntries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Path))))
                throw new ProcessingUploadScanException(ProcessingUploadScanFailure.InvalidResponse,
                    "Student scan response is incomplete.");

            return page;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new ProcessingUploadScanException(ProcessingUploadScanFailure.Timeout, "Student scan timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProcessingUploadScanException(ProcessingUploadScanFailure.Transport, "Student scan transport failed.", ex);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ProcessingUploadScanException(ProcessingUploadScanFailure.InvalidResponse, "Student scan JSON is invalid.", ex);
        }
    }

    public async Task<RemoveProcessedImagesResult> RemoveProcessedImagesAsync(string recordId, List<string> imagePaths, CancellationToken ct = default)
    {
        try
        {
            var body = new { imagePaths };
            using var response = await _httpClient.PostAsJsonAsync($"/api/uploads/{recordId}/remove-processed", body, JsonOptions, ct);
            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<RemoveProcessedImagesResult>>(JsonOptions, ct);
            if (!response.IsSuccessStatusCode || envelope is null || !envelope.Success)
            {
                var msg = envelope?.Message ?? "RemoveProcessedImages failed";
                _logger.LogWarning("RemoveProcessedImages failed: {Message}, Status: {Status}", msg, response.StatusCode);
                return new RemoveProcessedImagesResult { Success = false, ErrorMessage = msg };
            }

            return envelope.Data ?? new RemoveProcessedImagesResult { Success = false };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "RemoveProcessedImages HTTP request failed");
            return new RemoveProcessedImagesResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<StudentsByAccountResult> GetStudentsByIdentityAccountIdStrictAsync(string accountId, CancellationToken ct = default)
    {
        // Authorization ownership resolution must distinguish "call failed"
        // from "account maps to no student", so failures throw instead of
        // degrading to an empty list. Timeouts (TaskCanceledException) and
        // payload errors propagate unchanged.
        using var response = await _httpClient.GetAsync($"/api/accounts/{accountId}/students", ct);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<StudentsByAccountResult>>(JsonOptions, ct);
        if (!response.IsSuccessStatusCode || envelope is null || !envelope.Success)
        {
            var msg = envelope?.Message ?? "GetStudentsByIdentityAccountIdStrict failed";
            _logger.LogWarning("GetStudentsByIdentityAccountIdStrict failed: {Message}, Status: {Status}", msg, response.StatusCode);
            throw new HttpRequestException(msg, null, response.StatusCode);
        }

        return envelope.Data ?? new StudentsByAccountResult();
    }

    // ===== Envelope for deserializing { success, data, message } responses =====

    private class ApiEnvelope<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public T? Data { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    // Payload of the returned-record creation response: { success, replayed, record }.
    private class CreateReturnedRecordOutcomeData
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("replayed")]
        public bool Replayed { get; set; }
    }
}

// ===== DI extension =====

public static class StudentHttpClientServiceCollectionExtensions
{
    public static IServiceCollection AddStudentHttpClient(this IServiceCollection services, string baseAddress)
    {
        services.AddHttpClient<IStudentHttpClient, StudentHttpClient>((sp, client) =>
        {
            client.BaseAddress = new Uri(baseAddress);
        });
        return services;
    }
}
