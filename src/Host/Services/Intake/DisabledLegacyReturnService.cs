using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Services;
using Emenda.Service.Intake;

namespace Emenda.Host.Services.Intake;

/// <summary>Reads protected history without creating or advancing any return operation.</summary>
public sealed class DisabledLegacyReturnService(DbContextOptions<MistakeDbContext> databaseOptions) : ILegacyReturnService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static SourceReturnView View(LegacyReturnEntity op) => new(op.ItemId, op.OperationId,
        op.Phase, op.ConfirmedRecordId, op.CompletedAt, op.ErrorKind);

    private async Task<LegacyReturnEntity?> ReadAsync(Guid itemId, CancellationToken ct)
    {
        try { return await new LegacyReturnJournal(databaseOptions).GetAsync(itemId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceIntakeException("database_unavailable", 503); }
    }

    public async Task<SourceReturnView?> GetAsync(Guid itemId, CancellationToken ct = default)
    {
        var op = await ReadAsync(itemId, ct);
        return op is null ? null : View(op);
    }

    public async Task<SourceReturnView> ReturnAsync(Guid itemId, string actor, string? reason, string? cause,
        int grade, int subject, CancellationToken ct = default)
    {
        var op = await ReadAsync(itemId, ct);
        if (op is null) throw new SourceIntakeException("legacy_returns_disabled", 503);
        var effectiveGrade = grade is >= 1 and <= 12 ? grade : op.Grade;
        var effectiveSubject = subject is >= 1 and <= 9 ? subject : op.Subject;
        var canonical = JsonSerializer.Serialize(new object?[] { actor, reason ?? "", cause, effectiveGrade, effectiveSubject }, Json);
        if (!string.Equals(op.Canonical, canonical, StringComparison.Ordinal) || op.Phase == "Conflict")
            throw new SourceIntakeException("return_payload_conflict", 409);
        if (op.Phase == "Completed") return View(op);
        throw new SourceIntakeException("legacy_returns_disabled", 503);
    }

    public async Task<SourceReturnView> ResumeAsync(Guid itemId, CancellationToken ct = default)
    {
        var op = await ReadAsync(itemId, ct);
        if (op?.Phase == "Completed") return View(op);
        throw new SourceIntakeException("legacy_returns_disabled", 503);
    }

    public Task<List<Guid>> PendingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new List<Guid>());
    }
}
