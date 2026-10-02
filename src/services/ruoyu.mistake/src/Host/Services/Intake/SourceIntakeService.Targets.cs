using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
namespace Ruoyu.Study.Mistake.Host.Services.Intake;

public sealed partial class SourceIntakeService
{
    public bool TargetsEnabled => targetOptions?.Enabled == true;
    private static string[] RequiredPaths(IEnumerable<SourceIntakeGroupEntity> groups) => groups
        .SelectMany(g => JsonSerializer.Deserialize<List<SourceRegionEntry>>(g.Regions,IntakeCanonical.Json) ?? [])
        .Select(r => r.SourceImagePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static async Task<List<SourceTargetView>> TargetViewsAsync(MistakeDbContext db,Guid source,Guid revision,CancellationToken ct)
    {
        var groups=await db.SourceIntakeGroups.AsNoTracking().Where(g=>g.SourceUploadId==source && g.ContentRevision==revision).ToListAsync(ct);
        var objects=await db.SourceTargetObjects.AsNoTracking().Where(o=>o.SourceUploadId==source && o.ContentRevision==revision).ToListAsync(ct);
        var selected=objects.Where(o=>o.SelectedAttemptId.HasValue).Select(o=>o.SelectedAttemptId!.Value).ToArray();
        var attempts=await db.SourceTargetAttempts.AsNoTracking().Where(a=>selected.Contains(a.AttemptId)).ToDictionaryAsync(a=>a.AttemptId,ct);
        return RequiredPaths(groups).Select(path=>
        {
            var obj=objects.SingleOrDefault(o=>o.SourcePath==path);
            var candidate=obj?.SelectedAttemptId is Guid id && attempts.TryGetValue(id,out var found) ? found : null;
            return new SourceTargetView(path,candidate?.Phase=="Verified" ? "Verified" : "Pending",candidate?.Phase=="Verified" ? candidate.TargetKey : null);
        }).ToList();
    }
    public async Task<List<(Guid SourceId,Guid Revision)>> PendingTargetsAsync(CancellationToken ct=default)
    {
        if(!TargetsEnabled) return [];
        await using var db=Open();
        var sources=await db.SourceIntakes.AsNoTracking().Where(s=>s.Materialized).Select(s=>new {s.SourceUploadId,s.ContentRevision}).ToListAsync(ct);
        var pending=new List<(Guid,Guid)>();
        foreach(var source in sources)
            if((await TargetViewsAsync(db,source.SourceUploadId,source.ContentRevision,ct)).Any(t=>t.State!="Verified"))
                pending.Add((source.SourceUploadId,source.ContentRevision));
        return pending;
    }
    public async Task<IntakeView> ResumeTargetsAsync(Guid sourceId,Guid revision,CancellationToken ct=default)
    {
        if(!TargetsEnabled) throw new SourceIntakeException("targets_disabled",503);
        Lease lease;
        try
        {
            await using var db=Open();
            await using var transaction=await db.Database.BeginTransactionAsync(ct);
            await LockSourceAsync(db,sourceId,ct);
            var source=await LockIntakeAsync(db,sourceId,revision,ct) ?? throw new SourceIntakeException("intake_not_found",404);
            var paths=await TargetViewsAsync(db,sourceId,revision,ct);
            if(paths.Count==0) throw new SourceIntakeException("targets_not_materialized",409);
            if(paths.All(t=>t.State=="Verified")) return (await GetAsync(sourceId,revision,ct))!;
            if(source.LeaseUntil>await DbNowAsync(db,ct)) throw new SourceIntakeException("intake_busy",409);
            source.OwnerId=Guid.NewGuid();source.Fence++;
            source.LeaseUntil=await DbNowAsync(db,ct)+TimeSpan.FromSeconds(60);
            await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
            lease=new(sourceId,revision,source.OwnerId,source.Fence,JsonSerializer.Deserialize<SourceManifest>(source.Manifest,IntakeCanonical.Json)!);
        }
        catch(SourceIntakeException) {throw;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(Exception ex) {throw new SourceIntakeException("target_claim_unknown",503,ex);}
        using var processing=CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stop=new CancellationTokenSource();
        var heartbeat=HeartbeatAsync(lease,processing,stop.Token,targets:true);
        try
        {
            await using var db=Open();
            var groups=await db.SourceIntakeGroups.AsNoTracking().Where(g=>g.SourceUploadId==sourceId && g.ContentRevision==revision).ToListAsync(processing.Token);
            foreach(var path in RequiredPaths(groups)) await ResumeObjectAsync(lease,path,processing.Token);
            var result=(await GetAsync(sourceId,revision,processing.Token))!;
            if(!result.TargetReady) throw new SourceIntakeException("targets_pending",503);
            return result;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) {throw new SourceIntakeException("lease_lost",409);}
        catch(SourceIntakeException) {throw;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(Exception ex)
        {
            // A committed publish can lose its response. Only a fresh durable read may establish success.
            try {var view=await GetAsync(sourceId,revision,CancellationToken.None);if(view?.TargetReady==true)return view;} catch(Exception) { }
            throw new SourceIntakeException("target_commit_unknown",503,ex);
        }
        finally
        {
            stop.Cancel();await heartbeat;
            try
            {
                await using var db=Open();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE source_intake SET lease_until=NULL WHERE source_upload_id={lease.Source} AND content_revision={lease.Revision} AND owner_id={lease.Owner} AND fence={lease.Fence}",CancellationToken.None);
            }
            catch(Exception) { /* The database lease expires without deleting any object. */ }
        }
    }
    private async Task EnsureTargetLeaseAsync(MistakeDbContext db,Lease lease,CancellationToken ct)
    {
        await LockSourceAsync(db,lease.Source,ct);
        var source=await LockIntakeAsync(db,lease.Source,lease.Revision,ct) ?? throw new SourceIntakeException("lease_lost");
        if(!TargetsEnabled || source.OwnerId!=lease.Owner || source.Fence!=lease.Fence || source.LeaseUntil<=await DbNowAsync(db,ct))
            throw new SourceIntakeException("lease_lost");
    }
    private async Task ResumeObjectAsync(Lease lease,string path,CancellationToken ct)
    {
        var image=lease.Manifest.Images.FirstOrDefault(i=>i.Path==path && i.Type=="mistake") ?? throw new SourceIntakeException("target_binding");
        if(ReservedSourceTargetPath.IsReserved(path)) throw new SourceIntakeException("reserved_target");
        List<SourceTargetAttemptEntity> candidates;
        await using(var db=Open())
        {
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await EnsureTargetLeaseAsync(db,lease,ct);
            var obj=await db.SourceTargetObjects.SingleOrDefaultAsync(o=>o.SourceUploadId==lease.Source && o.ContentRevision==lease.Revision && o.SourcePath==path,ct);
            if(obj?.SelectedAttemptId!=null) return;
            if(obj==null)
            {
                db.SourceTargetObjects.Add(new(){SourceUploadId=lease.Source,ContentRevision=lease.Revision,SourcePath=path,
                    SnapshotId=lease.Manifest.SnapshotId,StudentId=lease.Manifest.StudentId,ExpectedLength=image.Length,ExpectedSha256=image.Sha256});
                await db.SaveChangesAsync(ct);
            }
            candidates=await db.SourceTargetAttempts.AsNoTracking().Where(a=>a.SourceUploadId==lease.Source && a.ContentRevision==lease.Revision && a.SourcePath==path).OrderBy(a=>a.AttemptId).ToListAsync(ct);
            await tx.CommitAsync(ct);
        }
        foreach(var candidate in candidates)
        {
            // Never write another owner's or previously reserved key, even if it looks absent.
            if(await TryVerifyCandidateAsync(candidate.TargetKey,image,ct))
            {await PublishTargetAsync(lease,candidate,image,ct);return;}
        }
        var attempt=Guid.NewGuid();
        var fresh=new SourceTargetAttemptEntity {AttemptId=attempt,SourceUploadId=lease.Source,ContentRevision=lease.Revision,SourcePath=path,
            TargetKey=$"{ReservedSourceTargetPath.Prefix}{lease.Source:D}/{lease.Revision:D}/{attempt:D}/original",OwnerId=lease.Owner,Fence=lease.Fence,Phase="Reserved"};
        await using(var db=Open())
        {
            await using var tx=await db.Database.BeginTransactionAsync(ct);await EnsureTargetLeaseAsync(db,lease,ct);
            db.SourceTargetAttempts.Add(fresh);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        try {await oss.CopyObjectAsync(path,fresh.TargetKey);}
        catch(Exception ex)
        {
            try {await SetAttemptPhaseAsync(lease,fresh.AttemptId,"Unknown",CancellationToken.None);}catch(Exception) { }
            throw new SourceIntakeException("target_copy_unknown",503,ex);
        }
        ct.ThrowIfCancellationRequested();
        await SetAttemptPhaseAsync(lease,fresh.AttemptId,"Copied",ct);
        if(!await TryVerifyCandidateAsync(fresh.TargetKey,image,ct)) throw new SourceIntakeException("target_bytes_unavailable",503);
        await PublishTargetAsync(lease,fresh,image,ct);
    }
    private async Task SetAttemptPhaseAsync(Lease lease,Guid attempt,string phase,CancellationToken ct)
    {
        await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);
        await EnsureTargetLeaseAsync(db,lease,ct);
        var candidate=await db.SourceTargetAttempts.SingleAsync(a=>a.AttemptId==attempt,ct);
        candidate.Phase=phase;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    private async Task<bool> TryVerifyCandidateAsync(string key,ManifestImage expected,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var stream=await oss.DownloadAsync(key);using var memory=new MemoryStream();
            await stream.CopyToAsync(memory,ct);
            return memory.Length==expected.Length && Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant()==expected.Sha256;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(Exception) {return false;}
    }
    private async Task PublishTargetAsync(Lease lease,SourceTargetAttemptEntity attempt,ManifestImage expected,CancellationToken ct)
    {
        await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);
        await EnsureTargetLeaseAsync(db,lease,ct);
        var obj=await db.SourceTargetObjects.SingleAsync(o=>o.SourceUploadId==lease.Source && o.ContentRevision==lease.Revision && o.SourcePath==attempt.SourcePath,ct);
        if(obj.SelectedAttemptId!=null) return;
        var candidate=await db.SourceTargetAttempts.SingleAsync(a=>a.AttemptId==attempt.AttemptId,ct);
        candidate.OwnerId=lease.Owner;candidate.Fence=lease.Fence;candidate.Phase="Verified";
        candidate.VerifiedLength=expected.Length;candidate.VerifiedSha256=expected.Sha256;
        await db.SaveChangesAsync(ct);
        obj.SelectedAttemptId=candidate.AttemptId;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    private async Task<SourceTargetObjectEntity> SelectedTargetAsync(MistakeDbContext db,string path,CancellationToken ct)
    {
        var obj=await (from o in db.SourceTargetObjects.AsNoTracking() join a in db.SourceTargetAttempts.AsNoTracking()
            on o.SelectedAttemptId equals (Guid?)a.AttemptId where a.TargetKey==path && a.Phase=="Verified" select o).SingleOrDefaultAsync(ct)
            ?? throw new SourceIntakeException("target_not_selected",403);
        var groups=await db.SourceIntakeGroups.AsNoTracking().Where(g=>g.SourceUploadId==obj.SourceUploadId && g.ContentRevision==obj.ContentRevision).ToListAsync(ct);
        var ids=groups.Where(g=>RequiredPaths([g]).Contains(obj.SourcePath,StringComparer.Ordinal)).Select(g=>g.ItemId).ToArray();
        if(!await db.MistakeItems.AnyAsync(i=>ids.Contains(i.Id) && i.StudentId==obj.StudentId && i.SourceUploadId==obj.SourceUploadId.ToString("D"),ct))
            throw new SourceIntakeException("target_binding",403);
        return obj;
    }
}
