using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Service.Tests;

/// <summary>A mistake row as a release before schema v26 stored it: the common columns plus the source-specific ones.</summary>
internal sealed record PreUnifiedRow(MistakeItemEntity Item, string? AnalysisSnapshot = null, string? ConversationSource = null,
    string? ConversationCaller = null);

/// <summary>
/// Writes mistake rows into a database that is still below schema v24 (or, with <c>sourceCaller</c>, at v24/v25). The EF
/// model maps only the unified source columns, so an EF insert cannot reproduce such a schema; this writes the columns
/// that existed then, the way an older release did. On v24/v25 the trigger derives the unified columns from them.
/// </summary>
internal static class PreUnifiedRows
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task InsertAsync(MistakeDbContext db, params PreUnifiedRow[] rows)
    {
        foreach (var (i, analysis, source, caller) in rows)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO mistake_item(id, student_id, subject, grade, source_upload_id, source_regions, review_status, reviewer_id,
                    reviewed_at, root_cause, analysis_snapshot, question_id, created_at, updated_at,
                    conversation_source, conversation_active, conversation_caller)
                VALUES ({i.Id}, {i.StudentId}, {i.Subject}, {i.Grade}, {i.SourceUploadId}, {JsonSerializer.Serialize(i.SourceRegions, Json)}::jsonb,
                    {i.ReviewStatus}, {i.ReviewerId}, {i.ReviewedAt}, {i.RootCause}, {analysis}::jsonb, {i.QuestionId},
                    {i.CreatedAt}, {i.UpdatedAt}, {source}::jsonb, {i.ConversationActive}, {caller})
                """);
    }

    /// <summary>The same row on a v24/v25 schema, also presetting <c>source_caller</c> (kept for a deleted task's extraction).</summary>
    public static Task InsertWithCallerAsync(MistakeDbContext db, PreUnifiedRow row, string? sourceCaller)
    {
        var (i, analysis, source, caller) = row;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mistake_item(id, student_id, subject, grade, source_upload_id, source_regions, review_status, reviewer_id,
                reviewed_at, root_cause, analysis_snapshot, question_id, created_at, updated_at,
                conversation_source, conversation_active, conversation_caller, source_caller)
            VALUES ({i.Id}, {i.StudentId}, {i.Subject}, {i.Grade}, {i.SourceUploadId}, {JsonSerializer.Serialize(i.SourceRegions, Json)}::jsonb,
                {i.ReviewStatus}, {i.ReviewerId}, {i.ReviewedAt}, {i.RootCause}, {analysis}::jsonb, {i.QuestionId},
                {i.CreatedAt}, {i.UpdatedAt}, {source}::jsonb, {i.ConversationActive}, {caller}, {sourceCaller})
            """);
    }
}
