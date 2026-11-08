using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Service.Intake;
using Emenda.StudentClient;
namespace Emenda.Host.Services.Intake;

public sealed partial class SourceIntakeService
{
    public bool ReturnsEnabled => returnOptions?.Enabled == true;
    private static SourceReturnView ReturnView(SourceReturnEntity op) => new(op.ItemId,op.OperationId,
        op.Phase=="Prepared" && op.ErrorKind!=null ? "Unknown" : op.Phase,op.ConfirmedRecordId,op.CompletedAt,op.ErrorKind);
    private static string ReturnCanonical(string actor,string? reason,string? cause,int grade,int subject) =>
        IntakeCanonical.Encode(actor,reason ?? "",JsonSerializer.Serialize(cause),
            grade is >=1 and <=12 ? grade.ToString(CultureInfo.InvariantCulture) : "",
            subject is >=1 and <=9 ? subject.ToString(CultureInfo.InvariantCulture) : "");
    public async Task<SourceReturnView> ReturnAsync(Guid itemId,string actor,string? reason,string? cause,int grade,int subject,CancellationToken ct=default)
    {
        if(itemId==Guid.Empty || string.IsNullOrWhiteSpace(actor)) throw new SourceIntakeException("invalid_request",400);
        var canonical=ReturnCanonical(actor,reason,cause,grade,subject);
        await using(var db=Open())
        {
            var prior=await db.SourceReturns.AsNoTracking().SingleOrDefaultAsync(o=>o.ItemId==itemId,ct);
            if(prior!=null)
            {
                if(prior.Canonical!=canonical) throw new SourceIntakeException("return_payload_conflict",409);
                if(prior.Phase=="Completed") return ReturnView(prior);
                if(prior.Phase=="Conflict") throw new SourceIntakeException("return_payload_conflict",409);
            }
        }
        if(!ReturnsEnabled) throw new SourceIntakeException("returns_disabled",503);
        try
        {
            await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);
            var group=await db.SourceIntakeGroups.AsNoTracking().SingleOrDefaultAsync(g=>g.ItemId==itemId,ct)
                ?? throw new SourceIntakeException("item_not_managed",404);
            await LockSourceAsync(db,group.SourceUploadId,ct);
            var source=await LockIntakeAsync(db,group.SourceUploadId,group.ContentRevision,ct) ?? throw new SourceIntakeException("intake_not_found",404);
            var prior=await db.SourceReturns.AsNoTracking().SingleOrDefaultAsync(o=>o.ItemId==itemId,ct);
            if(prior!=null)
            {
                if(prior.Canonical!=canonical) throw new SourceIntakeException("return_payload_conflict",409);
                if(prior.Phase=="Completed") return ReturnView(prior);
                if(prior.Phase=="Conflict") throw new SourceIntakeException("return_payload_conflict",409);
            }
            else
            {
                if(source.LeaseUntil>await DbNowAsync(db,ct)) throw new SourceIntakeException("intake_busy",409);
                var item=await db.MistakeItems.FromSqlInterpolated($"SELECT * FROM mistake_item WHERE id={itemId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
                if(item.ReviewStatus!=(int)ReviewStatus.PendingReview) throw new SourceIntakeException("return_not_pending",409);
                if(await db.ReanalyzeJobs.AnyAsync(j=>j.ItemId==itemId && (j.Status==1 || j.Status==2),ct)) throw new SourceIntakeException("reanalysis_active",409);
                var paths=(JsonSerializer.Deserialize<List<SourceRegionEntry>>(group.Regions,IntakeCanonical.Json) ?? [])
                    .Select(r=>r.SourceImagePath).Distinct(StringComparer.Ordinal).ToArray();
                var targetObjects=await db.SourceTargetObjects.AsNoTracking().Where(o=>o.SourceUploadId==source.SourceUploadId && o.ContentRevision==source.ContentRevision && paths.Contains(o.SourcePath)).ToListAsync(ct);
                var selections=targetObjects.Select(o=>o.SelectedAttemptId).ToArray();
                var attempts=await db.SourceTargetAttempts.AsNoTracking().Where(a=>selections.Contains(a.AttemptId) && a.Phase=="Verified").ToDictionaryAsync(a=>a.AttemptId,ct);
                if(paths.Length==0 || targetObjects.Count!=paths.Length || targetObjects.Any(o=>o.SelectedAttemptId==null || !attempts.ContainsKey(o.SelectedAttemptId.Value)))
                    throw new SourceIntakeException("return_targets_pending",409);
                source.OwnerId=Guid.NewGuid();source.Fence++;source.LeaseUntil=await DbNowAsync(db,ct)+TimeSpan.FromSeconds(60);
                await db.SaveChangesAsync(ct);
                var op=new SourceReturnEntity {ItemId=itemId,OperationId=$"mistake-return:{itemId:D}",SourceUploadId=source.SourceUploadId,ContentRevision=source.ContentRevision,
                    SnapshotId=source.SnapshotId,StudentId=source.StudentId,Actor=actor,Canonical=canonical,OriginalItem=JsonSerializer.Serialize(item,IntakeCanonical.Json),
                    ReturnReason=reason ?? "",RootCause=cause,Grade=grade is >=1 and <=12 ? grade : null,Subject=subject is >=1 and <=9 ? subject : null,
                    OwnerId=source.OwnerId,Fence=source.Fence};
                db.SourceReturns.Add(op);await db.SaveChangesAsync(ct);
                for(var index=0;index<paths.Length;index++)
                {
                    var obj=targetObjects.Single(o=>o.SourcePath==paths[index]);var target=attempts[obj.SelectedAttemptId!.Value];
                    db.SourceReturnObjects.Add(new(){ItemId=itemId,Position=index,SourcePath=obj.SourcePath,TargetKey=target.TargetKey,
                        ExpectedLength=obj.ExpectedLength,ExpectedSha256=obj.ExpectedSha256});
                }
                await db.SaveChangesAsync(ct);
                // Intention is durable; release the same source processing lease so Resume can acquire it.
                source.LeaseUntil=null;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            }
        }
        catch(SourceIntakeException) {throw;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(Exception)
        {
            // Never infer rollback from a missing COMMIT response.
            await using var fresh=Open();var saved=await fresh.SourceReturns.AsNoTracking().SingleOrDefaultAsync(o=>o.ItemId==itemId,CancellationToken.None);
            if(saved==null || saved.Canonical!=canonical) throw new SourceIntakeException("return_commit_unknown",503);
        }
        return await ResumeReturnAsync(itemId,ct);
    }
    public async Task<List<Guid>> PendingReturnsAsync(CancellationToken ct=default)
    {
        if(!ReturnsEnabled) return [];
        await using var db=Open();return await db.SourceReturns.AsNoTracking().Where(o=>o.Phase=="Prepared" || o.Phase=="Sending").OrderBy(o=>o.ItemId).Select(o=>o.ItemId).ToListAsync(ct);
    }
    private async Task<SourceReturnEntity> ReadReturnAsync(Guid item,CancellationToken ct)
    {await using var db=Open();return await db.SourceReturns.AsNoTracking().SingleOrDefaultAsync(o=>o.ItemId==item,ct) ?? throw new SourceIntakeException("return_not_found",404);}
    private async Task EnsureReturnLeaseAsync(MistakeDbContext db,Lease lease,Guid item,CancellationToken ct)
    {
        await LockSourceAsync(db,lease.Source,ct);var src=await LockIntakeAsync(db,lease.Source,lease.Revision,ct) ?? throw new SourceIntakeException("lease_lost",409);
        if(!ReturnsEnabled || src.OwnerId!=lease.Owner || src.Fence!=lease.Fence || src.LeaseUntil<=await DbNowAsync(db,ct)) throw new SourceIntakeException("lease_lost",409);
        var op=await db.SourceReturns.SingleAsync(o=>o.ItemId==item,ct);
        if(op.OwnerId!=lease.Owner || op.Fence!=lease.Fence || op.Phase is "Completed" or "Conflict") throw new SourceIntakeException("lease_lost",409);
    }
    public async Task<SourceReturnView> ResumeReturnAsync(Guid itemId,CancellationToken ct=default)
    {
        var op=await ReadReturnAsync(itemId,ct);
        if(op.Phase=="Completed") return ReturnView(op);
        if(op.Phase=="Conflict") throw new SourceIntakeException("return_payload_conflict",409);
        if(!ReturnsEnabled || returns==null) throw new SourceIntakeException("returns_disabled",503);
        Lease lease;
        try
        {
            await using(var db=Open())
            {
                await using var tx=await db.Database.BeginTransactionAsync(ct);await LockSourceAsync(db,op.SourceUploadId,ct);
                var src=await LockIntakeAsync(db,op.SourceUploadId,op.ContentRevision,ct) ?? throw new SourceIntakeException("lease_lost",409);
                var current=await db.SourceReturns.SingleAsync(o=>o.ItemId==itemId,ct);
                if(current.Phase=="Completed") return ReturnView(current);
                if(current.Phase=="Conflict") throw new SourceIntakeException("return_payload_conflict",409);
                if(src.LeaseUntil>await DbNowAsync(db,ct)) return ReturnView(current);
                src.OwnerId=Guid.NewGuid();src.Fence++;src.LeaseUntil=await DbNowAsync(db,ct)+TimeSpan.FromSeconds(60);
                await db.SaveChangesAsync(ct);current.OwnerId=src.OwnerId;current.Fence=src.Fence;
                await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
                lease=new(src.SourceUploadId,src.ContentRevision,src.OwnerId,src.Fence,JsonSerializer.Deserialize<SourceManifest>(src.Manifest,IntakeCanonical.Json)!);
            }
        }
        catch(SourceIntakeException) {throw;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(Exception)
        {
            // A lost claim COMMIT response must not authorize external I/O.
            throw new SourceIntakeException("return_commit_unknown",503);
        }
        using var processing=CancellationTokenSource.CreateLinkedTokenSource(ct);using var stop=new CancellationTokenSource();
        var heartbeat=HeartbeatAsync(lease,processing,stop.Token,reverse:true);
        try
        {
            op=await ReadReturnAsync(itemId,processing.Token);
            if(op.Payload==null)
            {
                await using var read=Open();var objects=await read.SourceReturnObjects.AsNoTracking().Where(o=>o.ItemId==itemId).OrderBy(o=>o.Position).ToListAsync(processing.Token);
                foreach(var obj in objects) await CopyReturnObjectAsync(lease,obj,processing.Token);
                await BindReturnPayloadAsync(lease,itemId,processing.Token);
                op=await ReadReturnAsync(itemId,processing.Token);
            }
            var receipt=await returns.ReadAsync(op.OperationId,processing.Token);
            if(receipt==null)
            {
                var payload=JsonSerializer.Deserialize<CreateReturnedRecordRequest>(op.Payload!,IntakeCanonical.Json) ?? throw new SourceIntakeException("return_payload_binding",409);
                await VerifyReturnSelectionsAsync(itemId,processing.Token);
                await returns.PostAsync(op.StudentId,payload,processing.Token);
                receipt=await returns.ReadAsync(op.OperationId,processing.Token);
                if(receipt==null) { await ReturnErrorAsync(lease,itemId,"return_receipt_pending",false,processing.Token);return ReturnView(await ReadReturnAsync(itemId,processing.Token)); }
            }
            if(receipt.OperationId!=op.OperationId || receipt.StudentId!=op.StudentId || receipt.UploadRecordId==Guid.Empty
                || receipt.DigestVersion!="returned-payload-v1" || receipt.PayloadDigest!=op.PayloadDigest
                || !DateTimeOffset.TryParse(receipt.CreatedAt,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var receiptTime)
                || receiptTime.Offset!=TimeSpan.Zero || receiptTime.Year<1970
                || (op.ConfirmedRecordId.HasValue && op.ConfirmedRecordId!=receipt.UploadRecordId))
                throw new SourceIntakeException("return_receipt_conflict",409);
            await CompleteReturnAsync(lease,op,receipt.UploadRecordId,receiptTime,processing.Token);
            return ReturnView(await ReadReturnAsync(itemId,processing.Token));
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(OperationCanceledException) {throw new SourceIntakeException("lease_lost",409);}
        catch(SourceIntakeException error)
        {
            try {await ReturnErrorAsync(lease,itemId,error.ErrorKind,error.ErrorKind is "return_receipt_conflict" or "return_payload_conflict",CancellationToken.None);}catch(Exception) { }
            throw;
        }
        catch(Exception)
        {
            var saved=await ReadReturnAsync(itemId,CancellationToken.None);if(saved.Phase=="Completed") return ReturnView(saved);
            throw new SourceIntakeException("return_commit_unknown",503);
        }
        finally
        {
            stop.Cancel();await heartbeat;
            try {await using var db=Open();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE source_intake SET lease_until=NULL WHERE source_upload_id={lease.Source} AND content_revision={lease.Revision} AND owner_id={lease.Owner} AND fence={lease.Fence}",CancellationToken.None);}catch(Exception) { }
        }
    }
    private async Task CopyReturnObjectAsync(Lease lease,SourceReturnObjectEntity obj,CancellationToken ct)
    {
        if(obj.SelectedAttemptId.HasValue) return;
        var expected=new ManifestImage {Length=obj.ExpectedLength,Sha256=obj.ExpectedSha256};
        await using(var read=Open())
        {
            // Only a normally completed Copy with a durable settled marker can be reconsidered.
            var candidates=await read.SourceReturnAttempts.AsNoTracking().Where(a=>a.ItemId==obj.ItemId && a.Position==obj.Position && a.CopySettledAt!=null && (a.Phase=="CopySettled" || a.Phase=="Verified")).ToListAsync(ct);
            foreach(var candidate in candidates)
                if(await TryVerifyCandidateAsync(candidate.ObjectKey,expected,ct)) {await SelectReturnAsync(lease,obj,candidate.AttemptId,ct);return;}
        }
        if(!await TryVerifyCandidateAsync(obj.TargetKey,expected,ct)) throw new SourceIntakeException("return_target_bytes_unavailable",503);
        var attempt=Guid.NewGuid();var candidateNew=new SourceReturnAttemptEntity {AttemptId=attempt,ItemId=obj.ItemId,Position=obj.Position,
            ObjectKey=$"uploads/mistake-returns/{obj.ItemId:D}/{attempt:D}/original.jpg",OwnerId=lease.Owner,Fence=lease.Fence};
        await using(var db=Open())
        {
            await using var tx=await db.Database.BeginTransactionAsync(ct);await EnsureReturnLeaseAsync(db,lease,obj.ItemId,ct);
            db.SourceReturnAttempts.Add(candidateNew);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        // Cancellation stops awaiting, never turns an in-flight noncancelable Copy into a publishable key.
        try { await oss.CopyObjectAsync(obj.TargetKey,candidateNew.ObjectKey).WaitAsync(ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(Exception) {throw new SourceIntakeException("return_copy_unknown",503);}
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);await EnsureReturnLeaseAsync(db,lease,obj.ItemId,ct);
            var saved=await db.SourceReturnAttempts.SingleAsync(a=>a.AttemptId==attempt,ct);saved.Phase="CopySettled";saved.CopySettledAt=await DbNowAsync(db,ct);
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch(Exception)
        {
            await using var fresh=Open();var marker=await fresh.SourceReturnAttempts.AsNoTracking().SingleAsync(a=>a.AttemptId==attempt,ct);
            if(marker.CopySettledAt==null || marker.Phase!="CopySettled") throw new SourceIntakeException("return_copy_settle_unknown",503);
        }
        if(!await TryVerifyCandidateAsync(candidateNew.ObjectKey,expected,ct)) throw new SourceIntakeException("return_copy_bytes_unavailable",503);
        await SelectReturnAsync(lease,obj,attempt,ct);
    }
    private async Task SelectReturnAsync(Lease lease,SourceReturnObjectEntity obj,Guid attempt,CancellationToken ct)
    {
        await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);await EnsureReturnLeaseAsync(db,lease,obj.ItemId,ct);
        var saved=await db.SourceReturnObjects.SingleAsync(o=>o.ItemId==obj.ItemId && o.Position==obj.Position,ct);
        if(saved.SelectedAttemptId.HasValue) return;
        var candidate=await db.SourceReturnAttempts.SingleAsync(a=>a.AttemptId==attempt,ct);
        if(candidate.CopySettledAt==null) throw new SourceIntakeException("return_unverified",409);
        if(candidate.Phase!="Verified") {candidate.Phase="Verified";candidate.VerifiedLength=obj.ExpectedLength;candidate.VerifiedSha256=obj.ExpectedSha256;await db.SaveChangesAsync(ct);}
        saved.SelectedAttemptId=attempt;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    private async Task BindReturnPayloadAsync(Lease lease,Guid item,CancellationToken ct)
    {
        await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);await EnsureReturnLeaseAsync(db,lease,item,ct);
        var op=await db.SourceReturns.SingleAsync(o=>o.ItemId==item,ct);
        if(op.Payload!=null) return;
        var objects=await db.SourceReturnObjects.Where(o=>o.ItemId==item).OrderBy(o=>o.Position).ToListAsync(ct);
        if(objects.Any(o=>o.SelectedAttemptId==null)) throw new SourceIntakeException("return_objects_pending",503);
        var ids=objects.Select(o=>o.SelectedAttemptId!.Value).ToArray();var candidates=await db.SourceReturnAttempts.Where(a=>ids.Contains(a.AttemptId)).ToDictionaryAsync(a=>a.AttemptId,ct);
        var payload=new CreateReturnedRecordRequest {OperationId=op.OperationId,Comments="Rejected",ReturnReason=op.ReturnReason,
            ImageEntries=objects.Select(o=>new ImageEntryDto {Path=candidates[o.SelectedAttemptId!.Value].ObjectKey,Type="mistake"}).ToList()};
        op.Payload=JsonSerializer.Serialize(payload,IntakeCanonical.Json);op.PayloadDigest=SourceReturnClient.Digest(op.StudentId,payload);op.Phase="Sending";op.ErrorKind=null;
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    private async Task VerifyReturnSelectionsAsync(Guid item,CancellationToken ct)
    {
        await using var db=Open();var objects=await db.SourceReturnObjects.AsNoTracking().Where(o=>o.ItemId==item).ToListAsync(ct);
        foreach(var obj in objects)
        {
            var candidate=await db.SourceReturnAttempts.AsNoTracking().SingleAsync(a=>a.AttemptId==obj.SelectedAttemptId,ct);
            if(candidate.CopySettledAt==null || candidate.Phase!="Verified" || !await TryVerifyCandidateAsync(candidate.ObjectKey,new ManifestImage{Length=obj.ExpectedLength,Sha256=obj.ExpectedSha256},ct))
                throw new SourceIntakeException("return_selected_bytes_unavailable",503);
        }
    }
    private async Task ReturnErrorAsync(Lease lease,Guid item,string error,bool conflict,CancellationToken ct)
    {
        await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);await EnsureReturnLeaseAsync(db,lease,item,ct);
        var op=await db.SourceReturns.SingleAsync(o=>o.ItemId==item,ct);op.ErrorKind=error;if(conflict)op.Phase="Conflict";
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    private async Task CompleteReturnAsync(Lease lease,SourceReturnEntity original,Guid record,DateTimeOffset receiptTime,CancellationToken ct)
    {
        await using var db=Open();await using var tx=await db.Database.BeginTransactionAsync(ct);await EnsureReturnLeaseAsync(db,lease,original.ItemId,ct);
        var op=await db.SourceReturns.SingleAsync(o=>o.ItemId==original.ItemId,ct);
        op.ConfirmedRecordId=record;op.ReceiptCreatedAt=receiptTime;op.CompletedAt=await DbNowAsync(db,ct);op.FinalLogId=Guid.NewGuid();op.Phase="Completed";op.ErrorKind=null;
        await db.SaveChangesAsync(ct);
        var item=await db.MistakeItems.SingleAsync(i=>i.Id==original.ItemId,ct);
        item.ReviewStatus=(int)ReviewStatus.Rejected;item.ReviewerId=op.Actor;item.ReviewedAt=op.CompletedAt;item.UpdatedAt=op.CompletedAt;
        if(op.RootCause!=null)item.RootCause=op.RootCause;if(op.Grade.HasValue)item.Grade=op.Grade.Value;if(op.Subject.HasValue)item.Subject=op.Subject.Value;
        db.ProcessLogs.Add(new(){Id=op.FinalLogId!.Value,MistakeItemId=item.Id,StudentId=item.StudentId,Action=(int)ProcessAction.ReviewRejected,
            Details="Managed source return completed; SourceRetained",CreatedAt=op.CompletedAt!.Value});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
