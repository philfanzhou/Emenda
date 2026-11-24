using Emenda.Domain.Models;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    // Cross-caller read: the endpoint must authorize the operator before calling.
    public async Task<ExtractionSummaryPage> ListSummariesAsync(int page = 1, CancellationToken ct = default, string? studentId = null)
    {
        const int pageSize = 20;
        const int maxPage = 100000;
        if (page is < 1 or > maxPage || studentId?.Length > 36) throw new ExtractionException("invalid_request");
        studentId = string.IsNullOrWhiteSpace(studentId) ? null : studentId;
        await using var db = Open();
        // Publication clears reviewed_at when rejecting a non-incorrect projection.
        // An actual reviewer rejection sets reviewed_at and still counts as a current mistake.
        var rows = await db.Database.SqlQuery<ExtractionJobSummary>($"""
            WITH jobs AS (
                SELECT id, caller, student_id, created_at, status
                FROM extraction_job
                WHERE ({studentId}::text IS NULL OR student_id = {studentId})
                ORDER BY created_at DESC, id
                LIMIT {pageSize + 1} OFFSET {(page - 1) * pageSize}
            )
            SELECT j.id AS "Id", j.caller AS "Caller", j.student_id AS "StudentId",
                   j.created_at AS "CreatedAt", j.status AS "Status",
                   counts.mistakes AS "MistakeCount", counts.confirmed AS "ConfirmedCount"
            FROM jobs j
            CROSS JOIN LATERAL (
                SELECT count(*)::int AS mistakes,
                       (count(*) FILTER (WHERE m.review_status = {(int)ReviewStatus.Confirmed}))::int AS confirmed
                FROM mistake_item m
                WHERE m.source_upload_id = 'extract:' || replace(j.id::text, '-', '')
                  AND (m.review_status <> {(int)ReviewStatus.Rejected} OR m.reviewed_at IS NOT NULL)
                  AND EXISTS (
                      SELECT 1 FROM extraction_question q
                      WHERE q.job_id = j.id AND q.item_id = m.id AND q.active
                  )
            ) counts
            ORDER BY j.created_at DESC, j.id
            """).ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);
        return new(rows, hasMore && page < maxPage ? page + 1 : null);
    }
}
