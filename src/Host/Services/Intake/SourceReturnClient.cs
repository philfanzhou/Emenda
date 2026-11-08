using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.StudentClient;
namespace Ruoyu.Study.Mistake.Host.Services.Intake;

public sealed class SourceReturnClient(HttpClient http, StudentServiceAuthenticationOptions identity)
{
    public sealed record Receipt(string OperationId, string StudentId, Guid UploadRecordId, string CreatedAt, string PayloadDigest, string DigestVersion);
    private sealed record Envelope(bool Success, Receipt? Data);
    public static string Digest(string student, CreateReturnedRecordRequest payload)
    {
        var canonical = new StringBuilder();
        void Append(string? value) { value ??= ""; canonical.Append(value.Length).Append(':').Append(value).Append('|'); }
        Append(student); Append(payload.Comments); Append(payload.ReturnReason);
        foreach (var image in payload.ImageEntries) { Append(image.Path); Append(image.Type); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
    public async Task<Receipt?> ReadAsync(string operation, CancellationToken ct)
    {
        if (!identity.Enabled) throw new SourceIntakeException("return_provider_unavailable",503);
        try
        {
            using var response = await http.GetAsync("/api/integrations/mistake/returns/" + operation + "/receipt",ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new SourceIntakeException("return_receipt_unavailable",503);
            var envelope = await response.Content.ReadFromJsonAsync<Envelope>(IntakeCanonical.Json,ct);
            if (envelope?.Success != true || envelope.Data == null) throw new SourceIntakeException("return_receipt_unavailable",503);
            return envelope.Data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceIntakeException) { throw; }
        catch (Exception) { throw new SourceIntakeException("return_receipt_unavailable",503); }
    }
    public async Task PostAsync(string student, CreateReturnedRecordRequest payload, CancellationToken ct)
    {
        if (!identity.Enabled) throw new SourceIntakeException("return_provider_unavailable",503);
        try
        {
            using var response=await http.PostAsJsonAsync($"/api/students/{Uri.EscapeDataString(student)}/uploads/returned",payload,IntakeCanonical.Json,ct);
            if(response.StatusCode==HttpStatusCode.Conflict) throw new SourceIntakeException("return_payload_conflict",409);
            // The authenticated historical GET, never the POST response, establishes completion.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceIntakeException) { throw; }
        catch (Exception) { throw new SourceIntakeException("return_post_unknown",503); }
    }
}
