using Emenda.Database.Entities;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    private static bool Overlaps(ExtractionProposalRequest left, ExtractionProposalRequest right) =>
        left.Scope == "all" || right.Scope == "all"
        || left.Scope == "questions" && right.Scope == "questions" && left.QuestionIds.Intersect(right.QuestionIds).Any()
        || left.Scope == "regions" && right.Scope == "regions" && left.SourceIds.Intersect(right.SourceIds).Any();

    private static ExtractionProposalView ProposalView(ExtractionProposalEntity row)
    {
        var request = row.RequestJson is null ? null : Payload.Read<ExtractionProposalRequest>(row.RequestJson);
        return new(row.Id, row.JobId, row.RequestKey, row.ExpectedVersion, row.Status, request?.Scope,
            request?.QuestionIds ?? [], row.ResultJson is null ? [] : Payload.Read<List<ExtractionQuestionDraft>>(row.ResultJson), row.ErrorCode)
        { Request = request };
    }

    public async Task<ExtractionProposalView> GenerateProposalAsync(ExtractionAccess access, Guid id,
        ExtractionProposalRequest request, CancellationToken ct = default)
    {
        if (request is null || request.RequestKey == Guid.Empty || request.ExpectedVersion < 1
            || string.IsNullOrWhiteSpace(request.Instruction) || request.Instruction.Length > 4000
            || request.Scope is not ("questions" or "regions" or "all") || request.QuestionIds is null || request.SourceIds is null || request.Regions is null
            || request.QuestionIds.Contains(Guid.Empty) || request.SourceIds.Contains(Guid.Empty)
            || request.QuestionIds.Distinct().Count() != request.QuestionIds.Count || request.SourceIds.Distinct().Count() != request.SourceIds.Count
            || request.Scope == "questions" && request.QuestionIds.Count == 0
            || request.Scope != "questions" && request.QuestionIds.Count > 0
            || request.Scope == "regions" && request.SourceIds.Count == 0 || request.Regions.Count > 256)
            throw new ExtractionException("invalid_request");
        if (!options.Value.Enabled) throw new ExtractionException("extraction_disabled", 503);
        var hash = Payload.HashJson(request);
        Guid proposalId, owner;
        int? grade;
        List<ExtractionSource> sources;
        List<ExtractionQuestionDraft> context;
        HashSet<int> subjects;
        await using (var jobLock = await SerializeJobAsync(id, ct))
        await using (var db = Open())
        {
            var job = await FindAsync(db, access, id, ct);
            grade = job.Grade;
            var retained = await db.ExtractionProposals.SingleOrDefaultAsync(p => p.JobId == id && p.RequestKey == request.RequestKey, ct);
            if (retained is not null)
            {
                if (retained.PayloadHash != hash) throw new ExtractionException("request_payload_conflict", 409);
                if (retained.Status is not ("Generating" or "Failed")) return ProposalView(retained);
                if (retained.Status == "Generating" && retained.LeaseUntil > await NowAsync(db, ct)) return ProposalView(retained);
                if (retained.Attempts >= Rules.MaxAttempts) throw new ExtractionException("attempts_exhausted", 409);
            }
            else await EnsureKeyAvailableAsync(db, id, request.RequestKey, ct);
            if (job.Version != request.ExpectedVersion) throw new ExtractionException("version_conflict", 409);
            if (job.Status != "Succeeded") throw new ExtractionException("processing_in_progress", 409);
            var snapshot = await CurrentSnapshotAsync(db, job, ct);
            if (request.QuestionIds.Any(q => !snapshot.Questions.Any(v => v.Question.Id == q))) throw new ExtractionException("not_found", 404);
            context = snapshot.Questions.Where(q => request.Scope == "all" || request.QuestionIds.Contains(q.Question.Id)).Select(q => q.Question).ToList();
            subjects = context.Select(q => q.Subject).ToHashSet();
            if (request.PreviousProposalId is { } previousId)
            {
                var previous = await db.ExtractionProposals.AsNoTracking().SingleOrDefaultAsync(p => p.Id == previousId && p.JobId == id, ct)
                    ?? throw new ExtractionException("not_found", 404);
                if (previous.Status != "Pending" || previous.ExpectedVersion != job.Version) throw new ExtractionException("proposal_not_pending", 409);
                var basis = Payload.Read<ExtractionProposalRequest>(previous.RequestJson!);
                if (basis.Scope != request.Scope || !basis.QuestionIds.SequenceEqual(request.QuestionIds))
                    throw new ExtractionException("proposal_scope_conflict", 409);
                context = Payload.Read<List<ExtractionQuestionDraft>>(previous.ResultJson!);
            }
            var available = Sources(job);
            if (request.SourceIds.Any(s => !available.Any(a => a.Id == s))) throw new ExtractionException("invalid_region");
            var selected = request.SourceIds.Count == 0 ? available.Select(s => s.Id).ToHashSet()
                : request.SourceIds.Concat(context.SelectMany(q => q.Regions).Select(r => r.SourceId)).ToHashSet();
            sources = available.Where(s => selected.Contains(s.Id)).ToList();
            if (sources.Count is < 1 or > Rules.MaxSources) throw new ExtractionException("analysis_request_too_large", 413);
            foreach (var region in request.Regions)
                Rules.ValidateQuestions([new() { Id = Guid.NewGuid(), Subject = 1, Statement = "region hint", Regions = [region] }], sources, automatic: false);
            proposalId = retained?.Id ?? Guid.NewGuid();
            owner = Guid.NewGuid();
            var now = await NowAsync(db, ct);
            var row = retained ?? new ExtractionProposalEntity { Id = proposalId, JobId = id, RequestKey = request.RequestKey,
                ExpectedVersion = job.Version, PayloadHash = hash, CreatedAt = now };
            row.Status = "Generating";
            row.RequestJson = Payload.Serialize(request);
            row.ResultJson = null;
            row.ErrorCode = null;
            row.Owner = owner;
            row.LeaseUntil = now.AddSeconds(options.Value.OperationTimeoutSeconds + 60);
            row.Attempts++;
            if (retained is null) db.ExtractionProposals.Add(row);
            await db.SaveChangesAsync(ct);
        }
        try
        {
            var analyzed = await AnalyzeRevisionAsync(grade, sources, context, request.Instruction, request.Scope, request.Regions, ct);
            var allowed = context.Select(q => q.Id).ToHashSet();
            if (analyzed.Questions.Any(q => q.Id != Guid.Empty && !allowed.Contains(q.Id))) throw new ExtractionException("foreign_question", 502);
            // A request to collect applies to a correct question only; elsewhere the flag is meaningless and dropped.
            var proposed = analyzed.Questions.Select((q, i) => (q.Id == Guid.Empty
                ? q with { Id = Payload.Identity($"{proposalId:N}:question:{i}") } : q) is var draft && draft.Outcome != "correct"
                ? draft with { IncludedByRequest = false } : draft).ToList();
            if (request.Scope == "questions" && proposed.Any(q => !subjects.Contains(q.Subject)))
                throw new ExtractionException("proposal_scope_conflict", 502);
            Rules.ValidateQuestions(proposed, sources, automatic: false);
            await using var jobLock = await SerializeJobAsync(id, ct);
            await using var db = Open();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var job = await FindAsync(db, access, id, ct);
            var row = await db.ExtractionProposals.SingleAsync(p => p.Id == proposalId, ct);
            if (job.Version != request.ExpectedVersion || row.Status != "Generating" || row.Owner != owner)
                throw new ExtractionException("version_conflict", 409);
            var pending = await db.ExtractionProposals.Where(p => p.JobId == id && p.Status == "Pending").ToListAsync(ct);
            var overlapping = pending.Where(p => p.Id == request.PreviousProposalId
                || Overlaps(request, Payload.Read<ExtractionProposalRequest>(p.RequestJson!))).ToList();
            // Independent question suggestions coexist; late responses cannot replace newer overlapping work.
            if (overlapping.Any(p => p.CreatedAt > row.CreatedAt))
            { row.Status = "Superseded"; row.RequestJson = null; }
            else
            {
                foreach (var previous in overlapping)
                {
                    previous.Status = "Superseded";
                    previous.RequestJson = null;
                    previous.ResultJson = null;
                }
                row.Status = "Pending";
                row.ResultJson = Payload.Serialize(proposed);
            }
            row.Owner = null;
            row.LeaseUntil = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ProposalView(row);
        }
        catch (Exception error)
        {
            // Persist failure without retaining generated geometry or leaking provider diagnostics.
            await using var db = Open();
            await db.ExtractionProposals.Where(p => p.Id == proposalId && p.Owner == owner && p.Status == "Generating")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Failed").SetProperty(p => p.RequestJson, (string?)null)
                    .SetProperty(p => p.ResultJson, (string?)null).SetProperty(p => p.Owner, (Guid?)null)
                    .SetProperty(p => p.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(p => p.ErrorCode, error is ExtractionException ? ((ExtractionException)error).Code : "proposal_failed"), CancellationToken.None);
            throw;
        }
    }

    /// <summary>Sends the verified originals, as size-bounded previews, with the current grouping and an instruction.</summary>
    private async Task<QuestionAnalysisResult> AnalyzeRevisionAsync(int? grade, List<ExtractionSource> sources,
        List<ExtractionQuestionDraft> context, string instruction, string scope, List<ExtractionRegion> regions, CancellationToken ct)
    {
        if (sources.Count is < 1 or > Rules.MaxSources) throw new ExtractionException("analysis_request_too_large", 413);
        var inputs = new List<QuestionAnalysisImage>();
        var previewBudget = (AiTutorQuestionClient.RequestLimit - Math.Max(1024 * 1024, sources.Count * 64 * 1024)
            - System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(context, Payload.Json).Length) / sources.Count * 3 / 4;
        if (previewBudget < 1024) throw new ExtractionException("analysis_request_too_large", 413);
        foreach (var source in sources)
        {
            var bytes = await VerifiedSourceAsync(source, ct);
            var preview = Emenda.Common.Oss.QuestionImage.Preview(bytes, previewBudget);
            inputs.Add(new(source.Id, "data:image/jpeg;base64," + Convert.ToBase64String(preview), source.Width, source.Height,
                regions.Where(r => r.SourceId == source.Id).ToList()));
        }
        return await analysis.AnalyzeAsync(new(grade, inputs) { Revision = new(instruction, scope, context) }, ct);
    }

    public async Task<List<ExtractionProposalView>> ListProposalsAsync(ExtractionAccess access, Guid id, CancellationToken ct = default)
    {
        await using var db = Open();
        await FindAsync(db, access, id, ct);
        return (await db.ExtractionProposals.AsNoTracking().Where(p => p.JobId == id && (p.Status == "Pending" || p.Status == "Generating"))
            .OrderByDescending(p => p.CreatedAt).ToListAsync(ct)).Select(ProposalView).ToList();
    }

    public async Task<ExtractionProposalView> GetProposalAsync(ExtractionAccess access, Guid id, Guid proposalId, CancellationToken ct = default)
    {
        await using var db = Open();
        await FindAsync(db, access, id, ct);
        return ProposalView(await db.ExtractionProposals.AsNoTracking().SingleOrDefaultAsync(p => p.Id == proposalId && p.JobId == id, ct)
            ?? throw new ExtractionException("not_found", 404));
    }

    public async Task<ExtractionProposalView> DiscardProposalAsync(ExtractionAccess access, Guid id, Guid proposalId, CancellationToken ct = default)
    {
        await using var jobLock = await SerializeJobAsync(id, ct);
        await using var db = Open();
        await FindAsync(db, access, id, ct);
        var row = await db.ExtractionProposals.SingleOrDefaultAsync(p => p.Id == proposalId && p.JobId == id, ct)
            ?? throw new ExtractionException("not_found", 404);
        if (row.Status == "Accepted") throw new ExtractionException("proposal_not_pending", 409);
        row.Status = "Discarded";
        row.RequestJson = row.ResultJson = null;
        row.Owner = null;
        row.LeaseUntil = null;
        await db.SaveChangesAsync(ct);
        return ProposalView(row);
    }
}
