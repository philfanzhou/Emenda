using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Emenda.HttpContracts;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;

namespace Emenda.Host.Services.Conversation;

public sealed class ConversationCollectionStore(DbContextOptions<MistakeDbContext> databaseOptions, IConversationAuthorization authorization, IOssService oss, IConversationExportReader exports)
{
    private static readonly string[] MaterializedStatuses = ["collected", "updated"];
    public Task<bool> AllowedAsync(string actor, string student, string owner, CancellationToken ct)
        => authorization.AllowedAsync(actor, student, owner, ct);
    public async Task AdmitAsync(string caller, string actor, Guid operation, string studentId, string owner, int grade, CancellationToken ct)
    {
        ValidateIdentity(actor, studentId, owner);
        if (operation == Guid.Empty || grade is < 0 or > 12) throw new ArgumentException("Invalid collection request.");
        if (!await AllowedAsync(actor, studentId, owner, ct)) throw new UnauthorizedAccessException();
        await using var db = new MistakeDbContext(databaseOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({operation.ToString("D")},871033))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({studentId},871032))", ct);
        var old = await db.ConversationCollections.FindAsync([operation], ct);
        if (old != null)
        {
            if (old.CallerName != caller || old.ActorId != actor || old.StudentId != studentId || old.OwnerId != owner || old.Grade != grade) throw new InvalidDataException("Collection operation conflict.");
            return;
        }
        if (await db.ConversationCollections.CountAsync(j => j.CallerName == caller && j.StudentId == studentId && (j.Status == "pending" || j.Status == "running"), ct) >= 20)
            throw new InvalidOperationException("Too many pending collection operations.");
        var now = DateTimeOffset.UtcNow;
        db.ConversationCollections.Add(new() { Id = operation, CallerName = caller, ActorId = actor, StudentId = studentId, OwnerId = owner, Grade = grade,
            CreatedAt = now, UpdatedAt = now, NextAttemptAt = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<ConversationCollectionEntity?> StatusAsync(string caller, string actor, string studentId, Guid operation, CancellationToken ct)
    {
        await using var db = new MistakeDbContext(databaseOptions);
        var job = await db.ConversationCollections.AsNoTracking().SingleOrDefaultAsync(j => j.Id == operation && j.CallerName == caller && j.ActorId == actor && j.StudentId == studentId, ct);
        if (job == null || !await AllowedAsync(job.ActorId, job.StudentId, job.OwnerId, ct)) return null;
        return job;
    }

    public async Task StopAsync(string caller, string actor, string id, CancellationToken ct)
    {
        ValidateIdentity(actor, id, "stop");
        await using var db = new MistakeDbContext(databaseOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Wait for accepted commits, then stop only exports belonging to cleared histories.
        var jobs = await db.ConversationCollections.FromSqlInterpolated($"SELECT * FROM conversation_collection_job WHERE caller_name={caller} AND student_id={id} AND status IN ('pending','running') FOR UPDATE").ToListAsync(ct);
        foreach (var job in jobs)
        {
            if (!await AllowedAsync(actor, id, job.OwnerId, ct)) throw new UnauthorizedAccessException();
            var source = await exports.ReadAsync(job.Id, job.OwnerId, true, ct);
            if (source.Status != "stopped") continue;
            job.Status = "stopped";
            job.ErrorCode = "HISTORY_CLEARED";
            job.LeaseId = null;
            job.LeaseUntil = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ReceiveAsync(Guid operation, Guid lease, ConversationExport packet, string fingerprint, CancellationToken ct)
    {
        await using var db = new MistakeDbContext(databaseOptions);
        var job = await db.ConversationCollections.SingleAsync(j => j.Id == operation, ct);
        if (job.LeaseId != lease || job.Status != "running" || !await AllowedAsync(job.ActorId, job.StudentId, job.OwnerId, ct)) throw new UnauthorizedAccessException();
        packet.Validate(job.OwnerId);
        var images = new Dictionary<string, (byte[] Data, string Media, string Name, string Folder)>();
        var descriptors = new List<ConversationQuestionImage>();
        var eventIds = packet.Events.Select(e => ReceiptId(job.CallerName, e.Id)).ToArray();
        var knownEvents = await db.ConversationMistakeReceipts.Where(r => eventIds.Contains(r.EventId)).Select(r => r.EventId).ToListAsync(ct);
        var imageQuestions = new List<ConversationExportQuestion>();
        foreach (var item in packet.Events.Where(e => !knownEvents.Contains(ReceiptId(job.CallerName, e.Id)) && e.Question.Attempt != null))
        {
            var key = SourceKey(job, packet.ConversationId, item.Question.Id);
            var id = new Guid(Convert.FromHexString(key[..32]));
            if (await db.MistakeItems.AnyAsync(i => i.Id == id, ct) || !await db.ConversationMistakeReceipts.AnyAsync(r => r.SourceKey == key && MaterializedStatuses.Contains(r.Status), ct)) imageQuestions.Add(item.Question);
        }
        foreach (var source in packet.Sources.Where(s => s.Image != null && imageQuestions.Any(q => q.SourcePositions.Contains(s.Position) || q.Attempt?.Position == s.Position))) AddImage(source.Position, null, null, source.Image!);
        foreach (var asset in packet.Assets.Where(a => imageQuestions.Any(q => q.AssetIds.Contains(a.Id)))) AddImage(asset.SourcePosition, asset.Id, [asset.Box.X1, asset.Box.Y1, asset.Box.X2, asset.Box.Y2], asset.Image);
        void AddImage(int position, Guid? asset, int[]? box, string image)
        {
            var decoded = ConversationExport.Image(image);
            var hash = Convert.ToHexStringLower(SHA256.HashData(decoded.Data));
            var folder = $"conversation/{ConversationExport.Hash(job.CallerName)}/{ConversationExport.Hash(job.StudentId)}/{packet.ConversationId:D}";
            var name = hash + "." + decoded.Extension;
            var path = $"mistakes/{folder}/{name}";
            images.TryAdd(path, (decoded.Data, decoded.MediaType, name, folder));
            descriptors.Add(new(position, asset, path, box));
        }
        // Publish durable references before any object write; failed/unknown writes remain protected.
        var staged = JsonSerializer.Deserialize<List<SourceRegionEntry>>(job.StagedRegions, ConversationExport.Json) ?? [];
        job.StagedRegions = JsonSerializer.Serialize(staged.Concat(images.Keys.Select(path => new SourceRegionEntry { SourceImagePath = path })).DistinctBy(r => r.SourceImagePath), ConversationExport.Json);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        foreach (var (path, image) in images)
        {
            ct.ThrowIfCancellationRequested();
            var saved = await oss.UploadAsync(image.Data, image.Name, image.Media, OssBucket.Mistakes, image.Folder).WaitAsync(ct);
            if (saved != path) throw new InvalidDataException("Unexpected stored conversation image.");
            await using var stored = await oss.DownloadAsync(path).WaitAsync(ct);
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stored, ct), SHA256.HashData(image.Data))) throw new InvalidDataException("Conversation image verification failed.");
        }
        if (!await AllowedAsync(job.ActorId, job.StudentId, job.OwnerId, ct)) throw new UnauthorizedAccessException();
        var current = await exports.ReadAsync(operation, job.OwnerId, true, ct);
        if (current.Status == "stopped") throw new ConversationSourceChangedException();
        if (current.Status != "completed" || current.Fingerprint != fingerprint) throw new InvalidOperationException("Source evidence changed; retry the same operation.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = (await db.ConversationCollections.FromSqlInterpolated($"SELECT * FROM conversation_collection_job WHERE id={operation} FOR UPDATE").AsNoTracking().ToListAsync(ct)).Single();
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (locked.Status != "running" || locked.LeaseId != lease || locked.LeaseUntil <= now) throw new InvalidOperationException("Collection lease expired.");
        if (!await AllowedAsync(job.ActorId, job.StudentId, job.OwnerId, ct)) throw new UnauthorizedAccessException();
        foreach (var item in packet.Events.OrderBy(e => e.Question.Id)) await ApplyAsync(db, job, packet, item, descriptors, ct);
        var receipts = new List<ConversationMistakeReceiptEntity>();
        foreach (var item in packet.Events) receipts.Add((await db.ConversationMistakeReceipts.FindAsync([ReceiptId(job.CallerName, item.Id)], ct))!);
        job.Result = JsonSerializer.Serialize(new { collected = receipts.Count(r => r.Status == "collected"), updated = receipts.Count(r => r.Status == "updated"), retracted = receipts.Count(r => r.Status == "retracted") });
        job.Status = "completed";
        job.ErrorCode = null;
        job.LeaseId = null;
        job.LeaseUntil = null;
        job.StagedRegions = "[]";
        job.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        // Evidence images get their display variants from the background backlog.
        await ImageVariantSchema.EnqueueAsync(db, images.Keys, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task ApplyAsync(MistakeDbContext db, ConversationCollectionEntity job, ConversationExport packet, ConversationExportEvent item,
        List<ConversationQuestionImage> images, CancellationToken ct)
    {
        var q = item.Question;
        var key = SourceKey(job, packet.ConversationId, q.Id);
        var fingerprint = ConversationExport.Hash(JsonSerializer.Serialize(new { item, sources = packet.Sources.Where(s => q.SourcePositions.Contains(s.Position) || s.Position == q.Attempt?.Position),
            assets = packet.Assets.Where(a => q.AssetIds.Contains(a.Id)) }, ConversationExport.Json));
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},871031))", ct);
        var replay = await db.ConversationMistakeReceipts.FindAsync([ReceiptId(job.CallerName, item.Id)], ct);
        if (replay != null)
        {
            if (replay.ContentHash != fingerprint || replay.StudentId != job.StudentId)
                throw new InvalidDataException("Conversation receipt conflict.");
            return;
        }
        var prior = await db.ConversationMistakeReceipts.Where(r => r.SourceKey == key).OrderByDescending(r => r.Position).ToListAsync(ct);
        var id = new Guid(Convert.FromHexString(key[..32]));
        var existing = (await db.MistakeItems.FromSqlInterpolated($"SELECT * FROM mistake_item WHERE id={id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        var snapshotJson = existing == null ? null : MistakeSource.ConversationSnapshot(existing);
        var previous = snapshotJson == null ? null : JsonSerializer.Deserialize<ConversationQuestionSnapshot>(snapshotJson, ConversationExport.Json);
        var selectedImages = images.Where(i => i.AssetId is { } asset ? q.AssetIds.Contains(asset) : q.SourcePositions.Contains(i.SourcePosition) || i.SourcePosition == q.Attempt?.Position).ToArray();
        var selectedSources = packet.Sources.Where(s => q.SourcePositions.Contains(s.Position) || s.Position == q.Attempt?.Position).Select(s => new ConversationQuestionSource(s.Position, s.Content)).ToArray();
        var observation = q.Attempt == null ? null : new Observation(new(item.Position, q.Attempt, q.Outcome, q.Evidence ?? "", q.RetractsMistake), selectedSources, selectedImages,
            selectedImages.Select(i => new SourceRegionEntry { SourceImagePath = i.Path }).DistinctBy(i => i.SourceImagePath).ToArray());
        var observations = prior.Where(r => r.Observation != "{}").Select(r => JsonSerializer.Deserialize<Observation>(r.Observation, ConversationExport.Json)!).ToArray();
        var status = "skipped";
        if ((previous?.Attempts ?? []).Concat(observations.Select(o => o.Attempt)).Any(a => a.RetractsMistake && a.RecordedAtPosition >= item.Position && a.Attempt == q.Attempt)) status = "superseded";
        else if (existing == null && prior.Any(r => MaterializedStatuses.Contains(r.Status))) status = "deleted";
        else if (existing?.ReviewerId != null || existing != null && (existing.ReviewStatus != (int)ReviewStatus.PendingReview || previous?.MaterializedAt != existing.UpdatedAt)) status = "reviewed";
        else if (q.RetractsMistake && existing == null) status = "retracted";
        else if (q.Outcome == "incorrect" || existing != null && q.Attempt != null)
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var later = previous != null && previous.Position > item.Position;
            var attempts = (previous?.Attempts ?? []).Concat(observations.Select(o => o.Attempt)).Concat(observation == null ? [] : [observation.Attempt])
                .DistinctBy(a => (a.RecordedAtPosition, a.Attempt, a.Outcome, a.RetractsMistake)).OrderBy(a => a.RecordedAtPosition).ToArray();
            var activeError = attempts.LastOrDefault(a => a.Outcome == "incorrect" && !a.RetractsMistake
                && !attempts.Any(r => r.RetractsMistake && r.RecordedAtPosition >= a.RecordedAtPosition && r.Attempt == a.Attempt));
            var snapshot = new ConversationQuestionSnapshot { ConversationId = packet.ConversationId, QuestionId = q.Id, Position = Math.Max(previous?.Position ?? 0, Math.Max(item.Position, attempts.LastOrDefault()?.RecordedAtPosition ?? 0)), MaterializedAt = timestamp, Statement = later ? previous!.Statement : q.Statement,
                StudentAttempt = activeError?.Attempt ?? previous?.StudentAttempt ?? q.Attempt,
                Evidence = activeError?.Evidence ?? previous?.Evidence ?? q.Evidence ?? "", ReferenceAnswer = later ? previous!.ReferenceAnswer : q.ReferenceAnswer ?? previous?.ReferenceAnswer ?? "",
                KnowledgePoints = later ? previous!.KnowledgePoints : q.KnowledgePoints,
                Sources = (previous?.Sources ?? []).Concat(observations.SelectMany(o => o.Sources)).Concat(selectedSources).DistinctBy(s => s.Position).ToArray(),
                Images = (previous?.Images ?? []).Concat(observations.SelectMany(o => o.Images)).Concat(selectedImages).DistinctBy(i => (i.SourcePosition, i.AssetId, i.Path)).ToArray(), Attempts = attempts };
            var entity = existing ?? new MistakeItemEntity { Id = id, StudentId = job.StudentId, Subject = q.Subject, Grade = job.Grade, ReviewStatus = (int)ReviewStatus.PendingReview, CreatedAt = DateTimeOffset.UtcNow };
            var serialized = JsonSerializer.Serialize(snapshot, ConversationExport.Json);
            if (snapshot.Images.Length > 100 || System.Text.Encoding.UTF8.GetByteCount(serialized) > 256 * 1024) throw new InvalidDataException("Conversation snapshot exceeds its budget.");
            // A collected mistake keeps the caller that first collected it.
            MistakeSource.SetConversation(entity, existing == null ? job.CallerName : existing.SourceCaller, serialized);
            entity.ConversationActive = activeError != null;
            entity.SourceRegions = snapshot.Images.Select(i => new SourceRegionEntry { SourceImagePath = i.Path }).DistinctBy(i => i.SourceImagePath).ToList();
            entity.UpdatedAt = timestamp;
            if (existing == null)
            {
                db.MistakeItems.Add(entity);
                // Like extraction publication, a collected mistake starts its process history with a Created entry.
                db.ProcessLogs.Add(new() { Id = Guid.NewGuid(), MistakeItemId = id, StudentId = job.StudentId, Action = (int)ProcessAction.Created,
                    CreatedAt = timestamp, Details = JsonSerializer.Serialize(new { operation = "conversation_collected", conversationId = packet.ConversationId,
                        questionId = q.Id, position = item.Position }, ConversationExport.Json) });
            }
            foreach (var receipt in prior) receipt.Observation = "{}";
            observation = null;
            status = q.RetractsMistake ? "retracted" : existing == null ? "collected" : "updated";
        }
        db.ConversationMistakeReceipts.Add(new() { EventId = ReceiptId(job.CallerName, item.Id), SourceKey = key, StudentId = job.StudentId, ItemId = id, Position = item.Position, ContentHash = fingerprint,
            Observation = observation == null || status is not ("skipped" or "retracted") ? "{}" : JsonSerializer.Serialize(observation, ConversationExport.Json), Status = status, CreatedAt = DateTimeOffset.UtcNow });
    }

    private static string SourceKey(ConversationCollectionEntity job, Guid conversationId, Guid questionId) =>
        ConversationExport.Hash($"{job.CallerName}\n{job.StudentId}\n{conversationId:D}\n{questionId:D}");

    private static string ReceiptId(string caller, string eventId) => ConversationExport.Hash(caller + "\n" + eventId);
    internal static void ValidateIdentity(string actor, string student, string owner)
    {
        if (!Valid(actor, 128) || !Valid(student, 36) || !Valid(owner, 256)) throw new ArgumentException("Invalid collection identity.");
        static bool Valid(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    }
    private sealed record Observation(ConversationQuestionAttempt Attempt, ConversationQuestionSource[] Sources, ConversationQuestionImage[] Images, SourceRegionEntry[] SourceRegions);
}
