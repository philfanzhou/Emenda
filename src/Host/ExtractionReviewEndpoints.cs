using Emenda.Database;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;
using Emenda.Service.Dtos;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host;

public static partial class ExtractionEndpoints
{
    private static async Task<IResult> ReviewAsync(HttpContext http, bool administrator,
        Guid id, Guid questionId, IExtractionService service, MistakeDbContext db,
        IMistakeDomainService mistakes, CancellationToken ct)
    {
        var access = Access(http, administrator);
        var request = await BodyAsync<ExtractionReviewRequest>(http, ct);
        var reviewer = access.Administrator ? access.Actor : request.ReviewerId;
        if (request.ExpectedVersion < 1 || request.ItemId == Guid.Empty || request.ReviewStatus is not (2 or 3) ||
            string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 36 || reviewer.Any(char.IsControl) ||
            request.RootCause?.Length > 8000 || request.ReturnReason?.Length > 2000 ||
            request.ReviewStatus == 3 && string.IsNullOrWhiteSpace(request.ReturnReason))
            throw new ExtractionException("invalid_request");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Revisions publish under this same job lock; the checked snapshot cannot change before commit.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ExtractionRules.LockKey(id.ToString())})", ct);
        var job = await service.GetAsync(access, id, ct);
        if (job.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
        var question = job.Questions.SingleOrDefault(view => view.Question.Id == questionId);
        if (question is null || question.ItemDeleted || question.ItemId != request.ItemId)
            throw new ExtractionException("question_not_found", 404);
        if (question.Question.Outcome != "incorrect") throw new ExtractionException("review_conflict", 409);
        var item = await mistakes.GetMistakeItemAsync(request.ItemId);
        if (item is null) throw new ExtractionException("question_not_found", 404);
        if (item.StudentId != job.StudentId || item.SourceUploadId != $"extract:{job.Id:N}" ||
            item.QuestionId != questionId.ToString("D") || item.Subject != question.Question.Subject)
            throw new ExtractionException("item_binding_conflict", 409);
        if (item.ReviewStatus != (int)ReviewStatus.PendingReview) throw new ExtractionException("review_conflict", 409);
        try
        {
            item = await mistakes.ReviewMistakeItemAsync(item.Id, request.ReviewStatus, reviewer, request.RootCause)
                ?? throw new ExtractionException("question_not_found", 404);
        }
        catch (MistakeValidationException) { throw new ExtractionException("review_conflict", 409); }
        if (request.ReviewStatus == (int)ReviewStatus.Rejected)
        {
            // The domain's new review log is tracked in this same scoped context and transaction.
            var reviewLog = db.ProcessLogs.Local.Single(log => log.MistakeItemId == item.Id && log.Action == (int)ProcessAction.ReviewRejected);
            reviewLog.Details = JsonSerializer.Serialize(new { operation = "extraction_review_rejected",
                version = job.Version, actor = reviewer, returnReason = request.ReturnReason });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Ok(new ReviewMistakeItemResponse { Success = true, StudentId = job.StudentId, SourceUploadId = $"extract:{job.Id:N}" });
    }
}
