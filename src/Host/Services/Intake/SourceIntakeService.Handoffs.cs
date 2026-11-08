using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Entities;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
namespace Ruoyu.Study.Mistake.Host.Services.Intake;

public sealed partial class SourceIntakeService
{
    public bool HandoffsEnabled=>handoffOptions?.Enabled==true;
    private static SourceHandoffView HandoffView(SourceHandoffEntity op)
    {
        var receipt=op.Receipt==null ? null : JsonSerializer.Deserialize<SourceHandoffReceipt>(op.Receipt,IntakeCanonical.Json);
        return new(op.OperationId,op.Phase,receipt?.ReceiptId,op.CompletedAt,op.ErrorKind);
    }
    public async Task<List<(Guid Source,Guid Revision,bool Sealed)>> PendingHandoffsAsync(CancellationToken ct=default)
    {
        if(!HandoffsEnabled)return [];
        await using var db=Open();
        var sources=await db.SourceIntakes.AsNoTracking().Where(s=>s.Mode=="Automatic" && s.Materialized).Select(s=>new {s.SourceUploadId,s.ContentRevision}).ToListAsync(ct);
        var saved=await db.SourceHandoffs.AsNoTracking().ToListAsync(ct);
        var pending=saved.Where(h=>h.Phase is "Prepared" or "Sending").Select(h=>(h.SourceUploadId,h.ContentRevision,true)).ToList();
        pending.AddRange(sources.Where(s=>!saved.Any(h=>h.SourceUploadId==s.SourceUploadId && h.ContentRevision==s.ContentRevision)).Select(s=>(s.SourceUploadId,s.ContentRevision,false)));
        return pending;
    }
    private async Task<SourceHandoffEntity> ReadHandoffAsync(Guid source,Guid revision,CancellationToken ct)
    {
        await using var db=Open();return await db.SourceHandoffs.AsNoTracking().SingleAsync(h=>h.SourceUploadId==source && h.ContentRevision==revision,ct);
    }
    public async Task<SourceHandoffView> HandoffAsync(Guid sourceId,Guid revision,string actor,bool automatic=false,CancellationToken ct=default)
    {
        if(sourceId==Guid.Empty || revision==Guid.Empty || string.IsNullOrWhiteSpace(actor))throw new SourceIntakeException("invalid_condition",400);
        await using(var read=Open())
        {
            var prior=await read.SourceHandoffs.AsNoTracking().SingleOrDefaultAsync(h=>h.SourceUploadId==sourceId && h.ContentRevision==revision,ct);
            if(prior?.Phase=="Completed")return HandoffView(prior);
            if(prior?.Phase=="Conflict")throw new SourceIntakeException("handoff_payload_conflict",409);
        }
        if(!HandoffsEnabled || handoffs==null)throw new SourceIntakeException("handoff_disabled",503);
        Lease lease;
        try
        {
            await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);
            await LockSourceAsync(db,sourceId,ct);var source=await LockIntakeAsync(db,sourceId,revision,ct) ?? throw new SourceIntakeException("intake_not_found",404);
            var op=await db.SourceHandoffs.SingleOrDefaultAsync(h=>h.SourceUploadId==sourceId && h.ContentRevision==revision,ct);
            if(op?.Phase=="Completed")return HandoffView(op);
            if(op?.Phase=="Conflict")throw new SourceIntakeException("handoff_payload_conflict",409);
            if(source.LeaseUntil>await DbNowAsync(db,ct))throw new SourceIntakeException("intake_busy",409);
            var manifest=JsonSerializer.Deserialize<SourceManifest>(source.Manifest,IntakeCanonical.Json)!;
            if(op==null)
            {
                if(automatic && source.Mode!="Automatic")throw new SourceIntakeException("source_mode_conflict",409);
                var groups=await db.SourceIntakeGroups.AsNoTracking().Where(g=>g.SourceUploadId==sourceId && g.ContentRevision==revision).ToListAsync(ct);
                var ids=groups.Select(g=>g.ItemId).ToArray();
                var items=await db.MistakeItems.AsNoTracking().Where(i=>ids.Contains(i.Id)).ToListAsync(ct);
                var returnsDone=await db.SourceReturns.AsNoTracking().Where(r=>ids.Contains(r.ItemId) && r.Phase=="Completed" && r.ConfirmedRecordId!=null && r.ReceiptCreatedAt!=null).Select(r=>r.ItemId).ToListAsync(ct);
                var paths=groups.SelectMany(g=>JsonSerializer.Deserialize<SourceRegionEntry[]>(g.Regions,IntakeCanonical.Json) ?? []).Select(r=>r.SourceImagePath).Distinct(StringComparer.Ordinal).ToArray();
                var required=manifest.Images.Where(i=>i.Type=="mistake").DistinctBy(i=>i.Path,StringComparer.Ordinal).ToArray();
                if(!source.Materialized || groups.Count==0 || items.Count!=groups.Count || required.Any(i=>!paths.Contains(i.Path,StringComparer.Ordinal))
                    || items.Any(i=>i.ReviewStatus!=(int)ReviewStatus.Confirmed && !(i.ReviewStatus==(int)ReviewStatus.Rejected && returnsDone.Contains(i.Id))))
                    throw new SourceIntakeException("handoff_not_ready",409);
                var objects=await db.SourceTargetObjects.AsNoTracking().Where(t=>t.SourceUploadId==sourceId && t.ContentRevision==revision && paths.Contains(t.SourcePath)).ToListAsync(ct);
                var selections=objects.Where(t=>t.SelectedAttemptId.HasValue).Select(t=>t.SelectedAttemptId!.Value).ToArray();
                var attempts=await db.SourceTargetAttempts.AsNoTracking().Where(t=>selections.Contains(t.AttemptId) && t.Phase=="Verified").ToDictionaryAsync(t=>t.AttemptId,ct);
                if(objects.Count!=required.Length || objects.Any(o=>o.SelectedAttemptId==null || !attempts.ContainsKey(o.SelectedAttemptId.Value)))throw new SourceIntakeException("handoff_targets_pending",409);
                var targets=required.OrderBy(i=>i.Path,StringComparer.Ordinal).Select(i=>
                {
                    var obj=objects.Single(o=>o.SourcePath==i.Path);
                    if(obj.ExpectedLength!=i.Length || obj.ExpectedSha256!=i.Sha256)throw new SourceIntakeException("handoff_targets_conflict",409);
                    return new SourceHandoffTarget(i.Path,attempts[obj.SelectedAttemptId!.Value].TargetKey,i.Length,i.Sha256);
                }).ToArray();
                var groupsCanonical=IntakeCanonical.Encode(new[]{"mistake-handoff-groups-v1"}.Concat(groups.OrderBy(g=>g.ItemId.ToString("D"),StringComparer.Ordinal).Select(g=>IntakeCanonical.Encode(g.ItemId.ToString("D"),g.Canonical))).ToArray());
                var groupsDigest=IntakeCanonical.Hash(groupsCanonical);
                var canonical=SourceHandoffCanonical.Payload(sourceId,revision,source.SnapshotId,source.StudentId,groupsDigest,targets);
                op=new() {SourceUploadId=sourceId,ContentRevision=revision,SnapshotId=source.SnapshotId,StudentId=source.StudentId,
                    OperationId=$"mistake-handoff:{source.SnapshotId:D}",Actor=actor,SealedAt=await DbNowAsync(db,ct),GroupsCanonical=groupsCanonical,GroupsDigest=groupsDigest,
                    Targets=JsonSerializer.Serialize(targets,IntakeCanonical.Json),PayloadCanonical=canonical,PayloadDigest=IntakeCanonical.Hash(canonical)};
                db.SourceHandoffs.Add(op);
            }
            source.OwnerId=Guid.NewGuid();source.Fence++;source.LeaseUntil=await DbNowAsync(db,ct)+TimeSpan.FromSeconds(60);
            // Publish source fence first; the operation guard independently verifies it.
            db.Entry(op).State=op.OwnerId==Guid.Empty ? EntityState.Detached : db.Entry(op).State;
            await db.SaveChangesAsync(ct);op.OwnerId=source.OwnerId;op.Fence=source.Fence;
            if(db.Entry(op).State==EntityState.Detached)db.SourceHandoffs.Add(op);
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            lease=new(sourceId,revision,source.OwnerId,source.Fence,manifest);
        }
        catch(SourceIntakeException){throw;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception e){throw new SourceIntakeException("handoff_commit_unknown",503,e);}
        using var processing=CancellationTokenSource.CreateLinkedTokenSource(ct);processing.CancelAfter(TimeSpan.FromSeconds(30));using var stop=new CancellationTokenSource();
        var heartbeat=HeartbeatAsync(lease,processing,stop.Token,forward:true);
        try
        {
            var op=await ReadHandoffAsync(sourceId,revision,processing.Token);
            var receipt=await handoffs.ReadAsync(sourceId,revision,processing.Token);
            if(receipt==null)
            {
                var targets=JsonSerializer.Deserialize<SourceHandoffTarget[]>(op.Targets,IntakeCanonical.Json)!;
                foreach(var target in targets)
                    if(!await VerifyHandoffTargetAsync(target,processing.Token))throw new SourceIntakeException("handoff_target_unavailable",503);
                await UpdateHandoffAsync(lease,"Sending",null,null,processing.Token);
                await handoffs.PutAsync(sourceId,revision,new(op.SnapshotId,op.GroupsDigest,targets),processing.Token);
                receipt=await handoffs.ReadAsync(sourceId,revision,processing.Token);
                if(receipt==null){await UpdateHandoffAsync(lease,"Sending","handoff_receipt_pending",null,processing.Token);return HandoffView(await ReadHandoffAsync(sourceId,revision,processing.Token));}
            }
            var remaining=lease.Manifest.Images.Count(i=>i.Type!="mistake");
            if(receipt.ReceiptId==Guid.Empty || receipt.OperationId!=op.OperationId || receipt.SourceUploadId!=sourceId || receipt.ContentRevision!=revision
                || receipt.SnapshotId!=op.SnapshotId || receipt.StudentId!=op.StudentId || receipt.GroupsDigest!=op.GroupsDigest
                || receipt.PayloadDigest!=op.PayloadDigest || receipt.DigestVersion!=SourceHandoffCanonical.Version
                || receipt.CommittedAt.Offset!=TimeSpan.Zero || receipt.CommittedAt.Year<1970 || receipt.RemainingImageCount!=remaining
                || receipt.RecordDeleted!=(remaining==0) || (remaining==0 ? receipt.ResultingContentRevision.HasValue : receipt.ResultingContentRevision is null || receipt.ResultingContentRevision==Guid.Empty || receipt.ResultingContentRevision==revision))
                throw new SourceIntakeException("handoff_receipt_conflict",409);
            await UpdateHandoffAsync(lease,"Completed",null,receipt,processing.Token);
            return HandoffView(await ReadHandoffAsync(sourceId,revision,processing.Token));
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(SourceIntakeException e)
        {
            try {await UpdateHandoffAsync(lease,e.ErrorKind is "handoff_payload_conflict" or "handoff_receipt_conflict" ? "Conflict" : null,e.ErrorKind,null,CancellationToken.None);}catch(Exception) { }
            throw;
        }
        catch(Exception)
        {
            var saved=await ReadHandoffAsync(sourceId,revision,CancellationToken.None);
            if(saved.Phase=="Completed")return HandoffView(saved);
            throw new SourceIntakeException("handoff_commit_unknown",503);
        }
        finally
        {
            stop.Cancel();await heartbeat;
            try {await using var db=Open();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE source_intake SET lease_until=NULL WHERE source_upload_id={lease.Source} AND content_revision={lease.Revision} AND owner_id={lease.Owner} AND fence={lease.Fence}",CancellationToken.None);}catch(Exception) { }
        }
    }
    private async Task<bool> VerifyHandoffTargetAsync(SourceHandoffTarget target,CancellationToken ct)
    {
        var download=oss.DownloadAsync(target.TargetKey);Stream stream;
        try {stream=await download.WaitAsync(ct);}
        catch(OperationCanceledException){_=download.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();},TaskScheduler.Default);throw;}
        await using(stream)
        {
            using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer=new byte[81920];long length=0;int n;
            while((n=await stream.ReadAsync(buffer,ct))!=0){hash.AppendData(buffer,0,n);length=checked(length+n);}
            return length==target.Length && Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()==target.Sha256;
        }
    }
    private async Task UpdateHandoffAsync(Lease lease,string? phase,string? error,SourceHandoffReceipt? receipt,CancellationToken ct)
    {
        await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);await LockSourceAsync(db,lease.Source,ct);
        var source=await LockIntakeAsync(db,lease.Source,lease.Revision,ct) ?? throw new SourceIntakeException("lease_lost",409);
        if(!HandoffsEnabled || source.OwnerId!=lease.Owner || source.Fence!=lease.Fence || source.LeaseUntil<=await DbNowAsync(db,ct))throw new SourceIntakeException("lease_lost",409);
        var op=await db.SourceHandoffs.SingleAsync(h=>h.SourceUploadId==lease.Source && h.ContentRevision==lease.Revision,ct);
        if(op.OwnerId!=lease.Owner || op.Fence!=lease.Fence || op.Phase is "Completed" or "Conflict")throw new SourceIntakeException("lease_lost",409);
        if(phase!=null)op.Phase=phase;op.ErrorKind=error;
        if(receipt!=null){op.Receipt=JsonSerializer.Serialize(receipt,IntakeCanonical.Json);op.CompletedAt=await DbNowAsync(db,ct);}
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
