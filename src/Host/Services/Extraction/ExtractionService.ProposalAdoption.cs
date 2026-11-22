using Emenda.Database;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;

namespace Emenda.Host.Services.Extraction;

public sealed partial class ExtractionService
{
    private sealed record ProposalAcceptance(Guid Id, List<ExtractionQuestionDraft>? Questions, string Hash);

    public async Task<ExtractionProposalAdoptionView> AdoptProposalAsync(ExtractionAccess access, Guid id, Guid proposalId,
        ExtractionProposalAdoptionRequest request, CancellationToken ct = default)
    {
        if (request is null || request.RequestKey == Guid.Empty || request.ExpectedVersion < 1 || proposalId == Guid.Empty)
            throw new ExtractionException("invalid_request");
        var accepted = await ReviseCoreAsync(access, id,
            new(request.RequestKey, request.ExpectedVersion, "adopt_proposal", []), null, ct,
            new(proposalId, request.Questions, ExtractionRules.Hash(new { proposalId, request })));
        await CollectJobAsync(id, ct);
        return new(proposalId, id, request.RequestKey, accepted.Version, accepted.Superseded);
    }

    private static async Task<List<ExtractionQuestionDraft>> ProposalRevisionAsync(MistakeDbContext db, Guid jobId,
        int version, ExtractionSnapshot prior, ProposalAcceptance acceptance, CancellationToken ct)
    {
        var proposal = await db.ExtractionProposals.AsNoTracking().SingleOrDefaultAsync(p => p.Id == acceptance.Id && p.JobId == jobId, ct)
            ?? throw new ExtractionException("not_found", 404);
        if (proposal.Status != "Pending" || proposal.ExpectedVersion != version)
            throw new ExtractionException("proposal_not_pending", 409);
        var request = ExtractionRules.Read<ExtractionProposalRequest>(proposal.RequestJson!);
        var candidate = ExtractionRules.Read<List<ExtractionQuestionDraft>>(proposal.ResultJson!);
        var proposed = acceptance.Questions ?? candidate;
        var allowedIds = candidate.Select(q => q.Id).Concat(request.Scope == "all"
            ? prior.Questions.Select(q => q.Question.Id) : request.QuestionIds).ToHashSet();
        if (proposed.Any(q => q is null || !allowedIds.Contains(q.Id))) throw new ExtractionException("proposal_scope_conflict", 409);
        var selected = prior.Questions.Where(q => request.QuestionIds.Contains(q.Question.Id)).ToList();
        if (request.Scope == "questions" && proposed.Any(q => !selected.Any(p => p.Question.Subject == q.Subject)))
            throw new ExtractionException("proposal_scope_conflict", 409);
        if (request.Scope == "all") return proposed;
        var merged = new List<ExtractionQuestionDraft>();
        var inserted = false;
        foreach (var current in prior.Questions)
        {
            if (!request.QuestionIds.Contains(current.Question.Id)) { merged.Add(current.Question); continue; }
            if (!inserted) { merged.AddRange(proposed); inserted = true; }
        }
        if (!inserted) merged.AddRange(proposed);
        return merged;
    }
}
