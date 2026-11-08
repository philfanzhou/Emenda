using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Host.Services.VlImageAnalysis;
using Emenda.Service.Intake;
using Emenda.Service.Services;
namespace Emenda.Host.Services.Intake;

public sealed partial class SourceIntakeService(DbContextOptions<MistakeDbContext> databaseOptions,
    SourceIntakeOptions options, SourceSnapshotClient snapshots, IOssService oss,
    IVlAnalyzer analyzer, IOptions<VlAnalyzerOptions> vlOptions, SourceTargetOptions? targetOptions = null, SourceReturnOptions? returnOptions = null, SourceReturnClient? returns = null, SourceHandoffOptions? handoffOptions = null, SourceHandoffClient? handoffs = null) : ISourceIntakeService, IMistakeSourceGuard, ISourceTargetService, ISourceReturnService, ISourceHandoffService
{
    public bool Enabled => options.Enabled;
    private MistakeDbContext Open() => new(databaseOptions);
    private sealed record Lease(Guid Source, Guid Revision, Guid Owner, long Fence, SourceManifest Manifest);
    private sealed record Plan(string Canonical, string[] Atoms, List<SourceRegionModel> Regions,
        int Subject, int Grade, string? Cause, string Payload);

    public async Task<List<MistakeItemModel>> CreateManualAsync(Guid sourceId, Guid revision, Guid requestKey,
        string actor, string studentId, int subject, int grade, string? rootCause,
        List<SourceRegionModel> regions, CancellationToken ct = default)
    {
        if (requestKey == Guid.Empty || string.IsNullOrWhiteSpace(actor)) throw new SourceIntakeException("invalid_request",400);
        var requestCanonical = IntakeCanonical.Request(sourceId, revision, studentId, subject, grade, rootCause, regions);
        var replay = await ReadReplayAsync(actor, requestKey, requestCanonical, ct);
        if (replay != null) return replay;
        if (!Enabled) throw new SourceIntakeException("intake_disabled",503);
        var manifest = await ManifestAsync(sourceId, revision, ct);
        if (manifest.StudentId != studentId) throw new SourceIntakeException("student_conflict",403);
        var group = IntakeCanonical.Group(manifest, regions);
        var plan = new Plan(group.Canonical, group.Atoms, group.Regions, subject, grade, rootCause,
            IntakeCanonical.Payload(subject,grade,rootCause));
        var (lease, existing) = await ClaimAsync(manifest, "Manual", actor, requestKey, requestCanonical, plan, ct);
        if (existing != null) return existing;
        if (lease == null) throw new SourceIntakeException("intake_busy",409);
        return await ExecuteOwnedAsync(lease, async token =>
        {
            await VerifyBytesAsync(manifest, token);
            return [plan];
        }, actor, requestKey, ct);
    }

    public async Task<List<MistakeItemModel>> CreateAutomaticAsync(Guid sourceId, Guid revision, CancellationToken ct = default)
    {
        if (sourceId == Guid.Empty || revision == Guid.Empty) throw new SourceIntakeException("invalid_condition",400);
        await using (var db = Open())
        {
            var current = await db.SourceIntakes.AsNoTracking().SingleOrDefaultAsync(s => s.SourceUploadId==sourceId && s.ContentRevision==revision,ct);
            if (current?.Mode == "Manual") throw new SourceIntakeException("source_mode_conflict");
            if (current?.Materialized == true) return await ResultsAsync(db,sourceId,revision,ct);
        }
        if (!Enabled) throw new SourceIntakeException("intake_disabled",503);
        var manifest = await ManifestAsync(sourceId,revision,ct);
        var (lease,existing) = await ClaimAsync(manifest,"Automatic",null,null,null,null,ct);
        if (existing != null) return existing;
        if (lease == null) throw new SourceIntakeException("intake_busy");
        return await ExecuteOwnedAsync(lease, async token =>
        {
            var bytes = await VerifyBytesAsync(manifest,token);
            var images = manifest.Images.Where(i=>i.Type=="mistake").DistinctBy(i=>i.Path,StringComparer.Ordinal).ToList();
            List<VlImageGroup> groups;
            if (!vlOptions.Value.IsConfigured)
                groups = [new() { ImageIndices=Enumerable.Range(0,images.Count).ToList(), Subject=1,Grade=7, RootCause="Mistake upload (VL skipped)" }];
            else
            {
                var analysis = await analyzer.AnalyzeVerifiedAsync(manifest.StudentId,images.Select(i=>i.Path).ToList(),images.Select(i=>bytes[i.Path]).ToList(),token);
                if (analysis.Skipped) throw new SourceIntakeException("invalid_analysis",503);
                groups=analysis.Groups;
            }
            // Reject omissions, bad indices, duplicate membership and bad payload. No fallback after configured VL failure.
            var indices = groups.SelectMany(g=>g.ImageIndices ?? []).ToArray();
            if (groups.Count==0 || groups.Any(g=>g.ImageIndices==null || g.ImageIndices.Count==0)
                || indices.Length!=images.Count || !indices.Order().SequenceEqual(Enumerable.Range(0,images.Count)))
                throw new SourceIntakeException("invalid_analysis",503);
            if (groups.Any(g=>g.Subject is < 1 or > 9 || g.Grade is < 1 or > 12)) throw new SourceIntakeException("invalid_analysis",503);
            return groups.Select(g=>
            {
                var selected=g.ImageIndices.Select(i=>new SourceRegionModel {SourceImagePath=images[i].Path}).ToList();
                var identity=IntakeCanonical.Group(manifest,selected);
                return new Plan(identity.Canonical,identity.Atoms,identity.Regions,g.Subject,g.Grade,g.RootCause,
                    IntakeCanonical.Payload(g.Subject,g.Grade,g.RootCause));
            }).ToList();
        },null,null,ct);
    }

    private async Task<SourceManifest> ManifestAsync(Guid source, Guid revision, CancellationToken ct)
    {
        await using var db=Open();
        var existing=await db.SourceIntakes.AsNoTracking().SingleOrDefaultAsync(s=>s.SourceUploadId==source && s.ContentRevision==revision,ct);
        if (existing!=null) return JsonSerializer.Deserialize<SourceManifest>(existing.Manifest,IntakeCanonical.Json)
            ?? throw new SourceIntakeException("invalid_source_manifest",503);
        return await snapshots.AcquireAsync(source,revision,ct);
    }

    private static Task LockSourceAsync(MistakeDbContext db,Guid source,CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({source.ToString("D")},791249))",ct);
    private static Task LockRequestAsync(MistakeDbContext db,string actor,Guid key,CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({IntakeCanonical.Encode(actor,"create",key.ToString("D"))},791250))",ct);
    private static async Task<SourceIntakeEntity?> LockIntakeAsync(MistakeDbContext db,Guid source,Guid revision,CancellationToken ct) =>
        await db.SourceIntakes.FromSqlInterpolated($"SELECT * FROM source_intake WHERE source_upload_id={source} AND content_revision={revision} FOR UPDATE").SingleOrDefaultAsync(ct);

    private async Task<(Lease? Lease,List<MistakeItemModel>? Existing)> ClaimAsync(SourceManifest manifest,string mode,
        string? actor,Guid? key,string? requestCanonical,Plan? plan,CancellationToken ct)
    {
        await using var db=Open();
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        if (actor!=null && key.HasValue) await LockRequestAsync(db,actor,key.Value,ct);
        await LockSourceAsync(db,manifest.SourceUploadId,ct);
        var source=await LockIntakeAsync(db,manifest.SourceUploadId,manifest.ContentRevision,ct);
        var created=source==null;
        if (source==null)
        {
            var legacyIds = await db.MistakeItems.FromSqlInterpolated($"SELECT * FROM mistake_item WHERE intake_source_key(source_upload_id)={manifest.SourceUploadId.ToString("D")}")
                .Select(i=>i.Id).ToListAsync(ct);
            var allowedIds=await (from g in db.SourceIntakeGroups join h in db.SourceHandoffs
                on new {g.SourceUploadId,g.ContentRevision} equals new {h.SourceUploadId,h.ContentRevision}
                where g.SourceUploadId==manifest.SourceUploadId && h.Phase=="Completed" && h.StudentId==manifest.StudentId select g.ItemId).ToListAsync(ct);
            legacyIds=legacyIds.Except(allowedIds).ToList();
            if (legacyIds.Count>0) throw new SourceIntakeException("legacy_source_conflict") { ConflictingItemIds=legacyIds };
            source=new SourceIntakeEntity {SourceUploadId=manifest.SourceUploadId,ContentRevision=manifest.ContentRevision,
                SnapshotId=manifest.SnapshotId,StudentId=manifest.StudentId,Manifest=JsonSerializer.Serialize(manifest,IntakeCanonical.Json),
                Mode=mode,OwnerId=Guid.NewGuid(),Fence=1};
            db.SourceIntakes.Add(source);
            source.LeaseUntil=await DbNowAsync(db,ct)+TimeSpan.FromSeconds(60);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            if (source.Manifest!=JsonSerializer.Serialize(manifest,IntakeCanonical.Json) || source.SnapshotId!=manifest.SnapshotId)
                throw new SourceIntakeException("manifest_conflict");
        }
        SourceIntakeRequestEntity? request=null;
        if (actor!=null && key.HasValue)
        {
            request=await db.SourceIntakeRequests.SingleOrDefaultAsync(r=>r.Actor==actor && r.Action=="create" && r.RequestKey==key,ct);
            if (request!=null && request.Canonical!=requestCanonical) throw new SourceIntakeException("request_payload_conflict");
            if (request?.ItemIds!=null) return (null,await ResultsByIdsAsync(db,request.ItemIds,ct));
            // Business identity aliases are resolved before mode, lease and external byte reads.
            var existing=await db.SourceIntakeGroups.Where(g=>g.SourceUploadId==manifest.SourceUploadId && g.ContentRevision==manifest.ContentRevision)
                .ToListAsync(ct);
            var same=existing.SingleOrDefault(g=>g.Canonical==plan!.Canonical);
            if (same!=null)
            {
                if (same.Payload!=plan!.Payload) throw new SourceIntakeException("payload_conflict");
                request ??= NewRequest(actor,key.Value,source,requestCanonical!);
                if (db.Entry(request).State==EntityState.Detached) db.SourceIntakeRequests.Add(request);
                request.ItemIds=[same.ItemId];
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return (null,[DeserializeResult(same)]);
            }
            if (await db.SourceHandoffs.AnyAsync(h=>h.SourceUploadId==source.SourceUploadId && h.ContentRevision==source.ContentRevision,ct))
                throw new SourceIntakeException("source_sealed",409);
            if (source.Mode=="Automatic") throw new SourceIntakeException("source_mode_conflict");
            if (existing.Any(g=>g.Atoms.Intersect(plan!.Atoms,StringComparer.Ordinal).Any())) throw new SourceIntakeException("grouping_conflict");
        }
        else
        {
            if (source.Mode!=mode) throw new SourceIntakeException("source_mode_conflict");
            if (source.Materialized) return (null,await ResultsAsync(db,source.SourceUploadId,source.ContentRevision,ct));
        }
        if (!created)
        {
            if (source.LeaseUntil>await DbNowAsync(db,ct)) throw new SourceIntakeException("intake_busy");
            source.OwnerId=Guid.NewGuid(); source.Fence++;
            source.LeaseUntil=await DbNowAsync(db,ct)+TimeSpan.FromSeconds(60);
        }
        if (actor!=null && key.HasValue && request==null) db.SourceIntakeRequests.Add(NewRequest(actor,key.Value,source,requestCanonical!));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (new Lease(source.SourceUploadId,source.ContentRevision,source.OwnerId,source.Fence,manifest),null);
    }

    private static SourceIntakeRequestEntity NewRequest(string actor,Guid key,SourceIntakeEntity source,string canonical) =>
        new() {Actor=actor,Action="create",RequestKey=key,SourceUploadId=source.SourceUploadId,ContentRevision=source.ContentRevision,Canonical=canonical};

    private async Task<List<MistakeItemModel>> ExecuteOwnedAsync(Lease lease,Func<CancellationToken,Task<List<Plan>>> build,
        string? actor,Guid? key,CancellationToken ct)
    {
        using var processing=CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeatStop=new CancellationTokenSource();
        var heartbeat=HeartbeatAsync(lease,processing,heartbeatStop.Token);
        try
        {
            var plans=await build(processing.Token);
            processing.Token.ThrowIfCancellationRequested();
            return await MaterializeAsync(lease,plans,actor,key,processing.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SourceIntakeException("lease_lost"); }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
            // Processing right only; never release Student snapshot or delete objects.
            if (Enabled)
            {
                try
                {
                    await using var db=Open();
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE source_intake SET lease_until=NULL WHERE source_upload_id={lease.Source} AND content_revision={lease.Revision} AND owner_id={lease.Owner} AND fence={lease.Fence}",CancellationToken.None);
                }
                catch (Exception) { /* Expiry retains the durable claim if the dependency is unavailable. */ }
            }
        }
    }

    private async Task HeartbeatAsync(Lease lease,CancellationTokenSource processing,CancellationToken stop, bool targets = false, bool reverse = false, bool forward = false)
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(stop))
            {
                if (!(forward ? HandoffsEnabled : reverse ? ReturnsEnabled : targets ? TargetsEnabled : Enabled)) { processing.Cancel(); return; }
                await using var db=Open();
                var changed=await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE source_intake SET lease_until=clock_timestamp()+interval '60 seconds' WHERE source_upload_id={lease.Source} AND content_revision={lease.Revision} AND owner_id={lease.Owner} AND fence={lease.Fence} AND lease_until>clock_timestamp()",stop);
                if (changed!=1) { processing.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { processing.Cancel(); }
    }

    private async Task<List<MistakeItemModel>> MaterializeAsync(Lease lease,List<Plan> plans,string? actor,Guid? key,CancellationToken ct)
    {
        try
        {
            await using var db=Open();
            await using var transaction=await db.Database.BeginTransactionAsync(ct);
            if (actor!=null && key.HasValue) await LockRequestAsync(db,actor,key.Value,ct);
            await LockSourceAsync(db,lease.Source,ct);
            var source=await LockIntakeAsync(db,lease.Source,lease.Revision,ct) ?? throw new SourceIntakeException("lease_lost");
            if (!Enabled || source.OwnerId!=lease.Owner || source.Fence!=lease.Fence || source.LeaseUntil<=await DbNowAsync(db,ct))
                throw new SourceIntakeException("lease_lost");
            if(await db.SourceHandoffs.AnyAsync(h=>h.SourceUploadId==lease.Source && h.ContentRevision==lease.Revision,ct)) throw new SourceIntakeException("source_sealed",409);
            var prior=await db.SourceIntakeGroups.Where(g=>g.SourceUploadId==lease.Source && g.ContentRevision==lease.Revision).ToListAsync(ct);
            var results=new List<MistakeItemModel>();
            foreach (var plan in plans)
            {
                if (prior.Any(g=>g.Atoms.Intersect(plan.Atoms,StringComparer.Ordinal).Any())) throw new SourceIntakeException("grouping_conflict");
                var item=new MistakeItemModel {Id=Guid.NewGuid(),StudentId=lease.Manifest.StudentId,SourceUploadId=lease.Source.ToString("D"),
                    Subject=plan.Subject,Grade=plan.Grade,RootCause=plan.Cause ?? "",SourceRegions=plan.Regions,ReviewStatus=(int)ReviewStatus.PendingReview,
                    CreatedAt=await DbNowAsync(db,ct)};
                var logId=Guid.NewGuid();
                var regions=JsonSerializer.Serialize(item.SourceRegions,IntakeCanonical.Json);
                var group=new SourceIntakeGroupEntity {ItemId=item.Id,SourceUploadId=lease.Source,ContentRevision=lease.Revision,
                    GroupHash=IntakeCanonical.Hash(plan.Canonical),Canonical=plan.Canonical,Atoms=plan.Atoms,Regions=regions,Payload=plan.Payload,
                    Result=JsonSerializer.Serialize(item,IntakeCanonical.Json),CreatedLogId=logId};
                // The restrictive result map is saved before the item; deferrable FKs check the complete transaction.
                db.SourceIntakeGroups.Add(group);
                await db.SaveChangesAsync(ct);
                db.MistakeItems.Add(new MistakeItemEntity {Id=item.Id,StudentId=item.StudentId,SourceUploadId=item.SourceUploadId,
                    Subject=item.Subject,Grade=item.Grade,RootCause=item.RootCause,ReviewStatus=item.ReviewStatus,CreatedAt=item.CreatedAt,
                    SourceRegions=JsonSerializer.Deserialize<List<SourceRegionEntry>>(regions,IntakeCanonical.Json)!});
                db.ProcessLogs.Add(new ProcessLogEntity {Id=logId,MistakeItemId=item.Id,StudentId=item.StudentId,Action=(int)ProcessAction.Created,
                    Details="Mistake source intake materialized; SourceRetained",CreatedAt=item.CreatedAt});
                await db.SaveChangesAsync(ct);
                results.Add(item);prior.Add(group);
            }
            source.Materialized=true;
            if (actor!=null && key.HasValue)
            {
                var request=await db.SourceIntakeRequests.SingleAsync(r=>r.Actor==actor && r.Action=="create" && r.RequestKey==key,ct);
                request.ItemIds=results.Select(i=>i.Id).ToArray();
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceIntakeException) { throw; }
        catch (Exception error)
        {
            // No compensation. COMMIT may have succeeded; fresh state is the only authority.
            try
            {
                await using var fresh=Open();
                if (actor!=null && key.HasValue)
                {
                    var request=await fresh.SourceIntakeRequests.AsNoTracking().SingleOrDefaultAsync(r=>r.Actor==actor && r.Action=="create" && r.RequestKey==key,CancellationToken.None);
                    if (request?.ItemIds!=null) return await ResultsByIdsAsync(fresh,request.ItemIds,CancellationToken.None);
                }
                else
                {
                    var source=await fresh.SourceIntakes.AsNoTracking().SingleOrDefaultAsync(s=>s.SourceUploadId==lease.Source && s.ContentRevision==lease.Revision,CancellationToken.None);
                    if (source?.Materialized==true) return await ResultsAsync(fresh,lease.Source,lease.Revision,CancellationToken.None);
                }
            }
            catch (Exception) { }
            throw new SourceIntakeException("intake_commit_unknown",503,error);
        }
    }

    private async Task<Dictionary<string,byte[]>> VerifyBytesAsync(SourceManifest manifest,CancellationToken ct)
    {
        var result=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        foreach (var image in manifest.Images.DistinctBy(i=>i.Path,StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream=await oss.DownloadAsync(image.Path);
                using var bytes=new MemoryStream();
                await stream.CopyToAsync(bytes,ct);
                var data=bytes.ToArray();
                if (data.LongLength!=image.Length || Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()!=image.Sha256)
                    throw new SourceIntakeException("source_bytes_conflict",503);
                result.Add(image.Path,data);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (SourceIntakeException) { throw; }
            catch (Exception) { throw new SourceIntakeException("source_unavailable",503); }
        }
        return result;
    }
    private static async Task<DateTimeOffset> DbNowAsync(MistakeDbContext db,CancellationToken ct) =>
        await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    private static MistakeItemModel DeserializeResult(SourceIntakeGroupEntity group) =>
        JsonSerializer.Deserialize<MistakeItemModel>(group.Result,IntakeCanonical.Json) ?? throw new SourceIntakeException("invalid_result",503);
    private static async Task<List<MistakeItemModel>> ResultsAsync(MistakeDbContext db,Guid source,Guid revision,CancellationToken ct) =>
        (await db.SourceIntakeGroups.AsNoTracking().Where(g=>g.SourceUploadId==source && g.ContentRevision==revision).OrderBy(g=>g.ItemId).ToListAsync(ct)).Select(DeserializeResult).ToList();
    private static async Task<List<MistakeItemModel>> ResultsByIdsAsync(MistakeDbContext db,Guid[] ids,CancellationToken ct)
    {
        var groups=await db.SourceIntakeGroups.AsNoTracking().Where(g=>ids.Contains(g.ItemId)).ToDictionaryAsync(g=>g.ItemId,ct);
        if (groups.Count!=ids.Length) throw new SourceIntakeException("invalid_result",503);
        return ids.Select(id=>DeserializeResult(groups[id])).ToList();
    }
    private async Task<List<MistakeItemModel>?> ReadReplayAsync(string actor,Guid key,string canonical,CancellationToken ct)
    {
        await using var db=Open();
        var request=await db.SourceIntakeRequests.AsNoTracking().SingleOrDefaultAsync(r=>r.Actor==actor && r.Action=="create" && r.RequestKey==key,ct);
        if (request!=null && request.Canonical!=canonical) throw new SourceIntakeException("request_payload_conflict");
        return request?.ItemIds==null ? null : await ResultsByIdsAsync(db,request.ItemIds,ct);
    }
    public async Task<string?> GetManagedItemStudentAsync(Guid itemId,CancellationToken ct=default)
    {
        await using var db=Open();
        return await (from result in db.SourceIntakeGroups join source in db.SourceIntakes
            on new {result.SourceUploadId,result.ContentRevision} equals new {source.SourceUploadId,source.ContentRevision}
            where result.ItemId==itemId select source.StudentId).SingleOrDefaultAsync(ct)
            ?? await db.LegacyReturns.AsNoTracking().Where(o => o.ItemId == itemId).Select(o => o.StudentId).SingleOrDefaultAsync(ct);
    }
    public async Task<bool> IsSourceManagedItemAsync(Guid itemId,CancellationToken ct=default)
    {
        await using var db=Open();
        return await db.SourceIntakeGroups.AnyAsync(g=>g.ItemId==itemId,ct);
    }
    public async Task<List<string>> GetManagedJobReviewersAsync(IReadOnlyList<Guid> jobIds,CancellationToken ct=default)
    {
        await using var db=Open();
        return await db.ReanalyzeJobs.AsNoTracking().Where(job => jobIds.Contains(job.Id)
            && (db.SourceIntakeGroups.Any(g => g.ItemId == job.ItemId) || db.LegacyReturns.Any(o => o.ItemId == job.ItemId)))
            .Select(job => job.ReviewerId).Distinct().ToListAsync(ct);
    }
    public async Task<bool> HasManagedDataAsync(string? studentId=null,string? sourceId=null,CancellationToken ct=default)
    {
        await using var db=Open();
        var legacy = db.MistakeItems.AsNoTracking().Where(i => db.LegacyReturns.Any(o => o.ItemId == i.Id));
        var query = db.SourceIntakes.AsNoTracking();
        if (!string.IsNullOrEmpty(studentId)) { query = query.Where(s => s.StudentId == studentId); legacy = legacy.Where(i => i.StudentId == studentId); }
        if (!string.IsNullOrEmpty(sourceId))
        {
            legacy = legacy.Where(i => i.SourceUploadId == sourceId);
            if (!Guid.TryParse(sourceId, out var id)) return await legacy.AnyAsync(ct);
            query = query.Where(s => s.SourceUploadId == id);
        }
        return await query.AnyAsync(ct) || await legacy.AnyAsync(ct);
    }
    public async Task<IntakeView?> GetAsync(Guid sourceId,Guid revision,CancellationToken ct=default)
    {
        await using var db=Open();
        var source=await db.SourceIntakes.AsNoTracking().SingleOrDefaultAsync(s=>s.SourceUploadId==sourceId && s.ContentRevision==revision,ct);
        if (source==null) return null;
        var ids=await db.SourceIntakeGroups.Where(g=>g.SourceUploadId==sourceId && g.ContentRevision==revision).Select(g=>g.ItemId).ToListAsync(ct);
        var targets=await TargetViewsAsync(db,sourceId,revision,ct);
        var handoff=await db.SourceHandoffs.AsNoTracking().SingleOrDefaultAsync(h=>h.SourceUploadId==sourceId && h.ContentRevision==revision,ct);
        return new(JsonSerializer.Deserialize<SourceManifest>(source.Manifest,IntakeCanonical.Json)!,source.Mode,source.Fence,source.LeaseUntil,source.Materialized,"SourceRetained",ids,targets,targets.Count>0 && targets.All(t=>t.State=="Verified"),handoff?.Phase=="Completed",(await db.SourceReturns.AsNoTracking().Where(o=>ids.Contains(o.ItemId)).ToListAsync(ct)).Select(ReturnView).ToList(),handoff==null ? null : HandoffView(handoff));
    }
    public async Task<string?> GetManagedObjectStudentAsync(string path,CancellationToken ct=default)
    {
        await using var db=Open();
        var legacy = await LegacyObjectAsync(db, path, ct);
        if (legacy is not null) return legacy.Value.Student;
        if(ReservedSourceTargetPath.IsReserved(path)) return (await SelectedTargetAsync(db,path,ct)).StudentId;
        var groups=await db.SourceIntakeGroups.AsNoTracking().ToListAsync(ct);
        var match=groups.FirstOrDefault(g=>JsonSerializer.Deserialize<List<SourceRegionEntry>>(g.Regions,IntakeCanonical.Json)!.Any(r=>r.SourceImagePath==path));
        if (match==null)
        {
            if (groups.Any(g=>JsonSerializer.Deserialize<List<SourceRegionEntry>>(g.Regions,IntakeCanonical.Json)!
                .Any(r=>string.Equals(r.SourceImagePath,path,StringComparison.OrdinalIgnoreCase))))
                throw new SourceIntakeException("object_not_managed",403);
            return null;
        }
        var source=await db.SourceIntakes.AsNoTracking().SingleAsync(s=>s.SourceUploadId==match.SourceUploadId && s.ContentRevision==match.ContentRevision,ct);
        var manifest=JsonSerializer.Deserialize<SourceManifest>(source.Manifest,IntakeCanonical.Json)!;
        return manifest.Images.Any(i=>i.Path==path && i.Type=="mistake") ? source.StudentId : null;
    }
    public async Task<bool> IsManagedPracticeObjectAsync(string path,CancellationToken ct=default)
    {
        await using var db=Open();
        var practices = await db.MistakePractices.AsNoTracking().Where(p => db.SourceIntakeGroups.Any(g => g.ItemId == p.MistakeItemId)
            || db.LegacyReturns.Any(o => o.ItemId == p.MistakeItemId)).Select(p => p.ImagePaths).ToListAsync(ct);
        if(practices.Any(paths=>paths.Contains(path,StringComparer.Ordinal))) return true;
        if(practices.Any(paths=>paths.Contains(path,StringComparer.OrdinalIgnoreCase)))
            throw new SourceIntakeException("object_not_managed",403);
        return false;
    }
    public async Task EnsureOwnedWriteAsync(Guid itemId,string studentId,CancellationToken ct=default)
    {
        var owner=await GetManagedItemStudentAsync(itemId,ct);
        if(owner!=null && owner!=studentId) throw new SourceIntakeException("student_binding",403);
    }
    public async Task VerifyManagedObjectAsync(string path,CancellationToken ct=default)
    {
        await using var db=Open();
        var legacy = await LegacyObjectAsync(db, path, ct);
        if (legacy is not null)
        {
            var obj = legacy.Value.Object;
            if (!obj.ExpectedLength.HasValue || !await TryVerifyCandidateAsync(path, new ManifestImage { Length = obj.ExpectedLength.Value, Sha256 = obj.ExpectedSha256! }, ct))
                throw new SourceIntakeException("return_bytes_unavailable", 503);
            return;
        }
        if(ReservedSourceTargetPath.IsReserved(path))
        {
            var selected=await SelectedTargetAsync(db,path,ct);
            if(!await TryVerifyCandidateAsync(path,new ManifestImage {Length=selected.ExpectedLength,Sha256=selected.ExpectedSha256},ct))
                throw new SourceIntakeException("target_bytes_conflict",503);
            return;
        }
        var sources=await db.SourceIntakes.AsNoTracking().ToListAsync(ct);
        var manifest=sources.Select(s=>JsonSerializer.Deserialize<SourceManifest>(s.Manifest,IntakeCanonical.Json)!)
            .FirstOrDefault(m=>m.Images.Any(i=>i.Path==path && i.Type=="mistake")) ?? throw new SourceIntakeException("object_not_managed",403);
        var image=manifest.Images.First(i=>i.Path==path);
        await VerifyBytesAsync(new SourceManifest { Images=[image] },ct);
    }
    public async Task EnsureLegacySourceAsync(string sourceId,CancellationToken ct=default)
    {
        if (!Guid.TryParse(sourceId,out var id)) return;
        await using var db=Open();
        if (await db.SourceIntakes.AnyAsync(s=>s.SourceUploadId==id,ct)) throw new SourceIntakeException("source_managed");
    }
    public async Task EnsureDestructiveItemAsync(Guid itemId,CancellationToken ct=default)
    {
        await using var db=Open();
        if (await db.LegacyReturns.AnyAsync(o => o.ItemId == itemId, ct)) throw new SourceIntakeException("return_in_progress", 409);
        if (await db.SourceIntakeGroups.AnyAsync(g=>g.ItemId==itemId,ct)) throw new SourceIntakeException("handoff_pending");
    }
    public async Task EnsureIdentityAsync(MistakeItemModel item,CancellationToken ct=default)
    {
        await using var db=Open();
        if (await db.LegacyReturns.AnyAsync(o => o.ItemId == item.Id, ct)) throw new SourceIntakeException("return_in_progress", 409);
        var group=await db.SourceIntakeGroups.AsNoTracking().SingleOrDefaultAsync(g=>g.ItemId==item.Id,ct);
        if (group==null) { await EnsureLegacySourceAsync(item.SourceUploadId,ct); return; }
        var original=DeserializeResult(group);
        if (item.StudentId!=original.StudentId || item.SourceUploadId!=original.SourceUploadId
            || JsonSerializer.Serialize(item.SourceRegions,IntakeCanonical.Json)!=JsonSerializer.Serialize(original.SourceRegions,IntakeCanonical.Json))
            throw new SourceIntakeException("source_managed");
    }
    private static async Task<(string Student, LegacyReturnObjectEntity Object)?> LegacyObjectAsync(MistakeDbContext db, string path, CancellationToken ct)
    {
        var objects = await db.LegacyReturnObjects.AsNoTracking().ToListAsync(ct);
        var attempts = await db.LegacyReturnAttempts.AsNoTracking().ToListAsync(ct);
        var source = objects.FirstOrDefault(o => o.SourcePath == path);
        var candidate = attempts.FirstOrDefault(a => a.ObjectKey == path);
        if (source is null && candidate is not null)
        {
            source = objects.Single(o => o.ItemId == candidate.ItemId && o.Position == candidate.Position);
            if (source.SelectedAttemptId != candidate.AttemptId || candidate.Phase != "Verified" || candidate.CopySettledAt is null)
                throw new SourceIntakeException("object_not_managed", 403);
        }
        if (source is null)
        {
            if (path.StartsWith("uploads/mistake-returns/", StringComparison.OrdinalIgnoreCase)
                || objects.Any(o => string.Equals(o.SourcePath, path, StringComparison.OrdinalIgnoreCase))
                || attempts.Any(a => string.Equals(a.ObjectKey, path, StringComparison.OrdinalIgnoreCase)))
                throw new SourceIntakeException("object_not_managed", 403);
            return null;
        }
        var owner = await db.LegacyReturns.AsNoTracking().Where(o => o.ItemId == source.ItemId).Select(o => o.StudentId).SingleAsync(ct);
        return (owner, source);
    }

}
