using Microsoft.EntityFrameworkCore;
using Emenda.Database;

namespace Emenda.Host.Services.Conversation;

/// <summary>Only registered, currently authorized operations may pull a fixed Tutor export.</summary>
public sealed class ConversationCollectionWorker(IServiceScopeFactory scopes, ILogger<ConversationCollectionWorker> logger, ConversationCollectionOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Conversation collection could not access its durable work queue."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public async Task RunOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ConversationCollectionStore>();
        var exports = scope.ServiceProvider.GetRequiredService<IConversationExportReader>();
        // Source operations expire after 24 hours; unmatched practice observations then have no live predecessor.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE conversation_mistake_receipt SET observation={"{}"}::jsonb WHERE observation<>{"{}"}::jsonb AND created_at<clock_timestamp()-interval '25 hours'", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE conversation_collection_job SET staged_regions='[]'::jsonb WHERE status IN ('failed','stopped') AND staged_regions<>'[]'::jsonb AND updated_at<clock_timestamp()-interval '25 hours'", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE conversation_collection_job SET status='failed',error_code='RETRY_LIMIT' WHERE status IN ('pending','running') AND (attempt>=120 OR created_at<clock_timestamp()-interval '24 hours') AND (lease_until IS NULL OR lease_until<clock_timestamp())", cancellationToken);
        var lease = Guid.NewGuid();
        var jobs = await db.ConversationCollections.FromSqlInterpolated($"""
            UPDATE conversation_collection_job SET status='running',lease_id={lease},lease_until=clock_timestamp()+interval '120 seconds',attempt=attempt+1,updated_at=clock_timestamp()
            WHERE id=(SELECT id FROM conversation_collection_job WHERE caller_name={options.CallerName} AND attempt<120 AND
              ((status='pending' AND next_attempt_at<=clock_timestamp()) OR (status='running' AND lease_until<clock_timestamp()))
              ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);
        var job = jobs.SingleOrDefault(); if (job == null) return;
        var status = "pending"; string? error = null;
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); processing.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = processing.Token;
        try
        {
            if (!await store.AllowedAsync(job.ActorId, job.StudentId, job.OwnerId, ct)) { status = "stopped"; error = "STUDENT_ACCESS_REVOKED"; }
            else
            {
                var result = await exports.ReadAsync(job.Id, job.OwnerId, false, ct);
                switch (result.Status)
                {
                    case "pending": break;
                    case "stopped": status = "stopped"; break;
                    case "failed": status = "failed"; error = "ORGANIZATION_FAILED"; break;
                    case "completed" when result.Result != null:
                        await store.ReceiveAsync(job.Id, lease, result.Result, result.Fingerprint!, ct); status = "completed"; break;
                    default: throw new InvalidDataException("Invalid collection status.");
                }
            }
        }
        catch (UnauthorizedAccessException) { status = "stopped"; error = "STUDENT_ACCESS_REVOKED"; }
        catch (ConversationSourceChangedException) { status = "stopped"; error = "SOURCE_CHANGED"; }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException or ImageMagick.MagickException)
        { status = "failed"; error = "INVALID_EVIDENCE"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or DbUpdateException or InvalidOperationException)
        { error = "COLLECTION_UNAVAILABLE"; }
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE conversation_collection_job SET status={status},error_code={error},lease_id=NULL,lease_until=NULL,
              next_attempt_at=clock_timestamp()+interval '10 seconds',updated_at=clock_timestamp()
            WHERE id={job.Id} AND status='running' AND lease_id={lease}
            """, finish.Token);
    }
}
