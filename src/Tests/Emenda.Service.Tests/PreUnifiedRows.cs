using System.Text.Json;
using Emenda.Database;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Service.Tests;

/// <summary>
/// Writes mistake rows into a database that is still below schema v24. The EF model maps the unified source
/// columns, so an EF insert would name columns such a schema does not have; this writes only the columns that
/// existed before v24, the way an older release did.
/// </summary>
internal static class PreUnifiedRows
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task InsertAsync(MistakeDbContext db, params MistakeItemEntity[] items)
    {
        foreach (var i in items)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO mistake_item(id, student_id, subject, grade, source_upload_id, source_regions, review_status, reviewer_id,
                    reviewed_at, root_cause, analysis_snapshot, question_id, created_at, updated_at,
                    conversation_source, conversation_active, conversation_caller)
                VALUES ({i.Id}, {i.StudentId}, {i.Subject}, {i.Grade}, {i.SourceUploadId}, {JsonSerializer.Serialize(i.SourceRegions, Json)}::jsonb,
                    {i.ReviewStatus}, {i.ReviewerId}, {i.ReviewedAt}, {i.RootCause}, {i.AnalysisSnapshot}::jsonb, {i.QuestionId},
                    {i.CreatedAt}, {i.UpdatedAt}, {i.ConversationSource}::jsonb, {i.ConversationActive}, {i.ConversationCaller})
                """);
    }
}
