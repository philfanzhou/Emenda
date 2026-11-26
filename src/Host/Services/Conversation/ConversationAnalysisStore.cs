using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.HttpContracts;

namespace Emenda.Host.Services.Conversation;

public sealed record ConversationAnalysisWrite(string StudentId, string ActorId, string EvidenceHash, int Version, string Model, string Content);

/// <summary>Model analysis is separate from the teacher's decision and current evidence.</summary>
public sealed class ConversationAnalysisStore(DbContextOptions<MistakeDbContext> options, IConversationAuthorization authorization)
{
    public async Task<bool> SaveAsync(string caller, Guid itemId, ConversationAnalysisWrite input, CancellationToken ct)
    {
        if (input.Version != 1 || input.EvidenceHash is not { Length: 64 } || !input.EvidenceHash.All(char.IsAsciiHexDigit)
            || input.Model is not { Length: > 0 and <= 32 } || input.Model.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || string.IsNullOrWhiteSpace(input.Content) || input.Content.EnumerateRunes().Count() > 8000) throw new ArgumentException("Invalid analysis.");
        ConversationCollectionStore.ValidateIdentity(input.ActorId, input.StudentId, "analysis");
        await using var db = new MistakeDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = (await db.MistakeItems.FromSqlInterpolated($"SELECT * FROM mistake_item WHERE id={itemId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (item is not { SourceKind: MistakeSource.Conversation, SourceDetail: not null } || item.SourceCaller != caller || item.StudentId != input.StudentId || item.ReviewStatus != (int)ReviewStatus.Confirmed
            && (item.ReviewStatus != (int)ReviewStatus.PendingReview || !item.ConversationActive)) throw new UnauthorizedAccessException();
        var provenance = await db.ConversationCollections.AsNoTracking().Where(j => j.CallerName == caller && j.StudentId == input.StudentId).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (provenance == null || !await authorization.AllowedAsync(input.ActorId, input.StudentId, provenance.OwnerId, ct)) throw new UnauthorizedAccessException();
        var snapshot = JsonSerializer.Deserialize<ConversationQuestionSnapshot>(item.SourceDetail, ConversationExport.Json) ?? throw new InvalidDataException("Missing evidence.");
        if (snapshot.EvidenceHash(item.Subject, item.ReviewStatus, item.RootCause,
            item.SourceRegions.Select(r => new ConversationEvidenceRegion(r.SourceImagePath, r.BoundingBox is { } b ? [b.X1, b.Y1, b.X2, b.Y2] : null))) != input.EvidenceHash) return false;
        if (snapshot.Analysis is { } old && old.EvidenceHash == input.EvidenceHash && old.Version == input.Version && old.Model == input.Model) return true;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        item.ConversationSource = JsonSerializer.Serialize(snapshot with { MaterializedAt = snapshot.MaterializedAt == item.UpdatedAt ? timestamp : snapshot.MaterializedAt,
            Analysis = new(input.EvidenceHash, input.Version, input.Model, input.Content) }, ConversationExport.Json);
        item.UpdatedAt = timestamp;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return true;
    }
}
