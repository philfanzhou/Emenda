using Microsoft.Extensions.Logging;
using Emenda.Domain.Models;
using Emenda.Domain.Repositories;

namespace Emenda.Domain.Services;

public class MistakeValidationException : Exception
{
    public MistakeValidationException(string message) : base(message) { }
}

/// <summary>A write that would change the identity of a source-backed item (HTTP 409 with <see cref="Code"/>).</summary>
public class MistakeConflictException : Exception
{
    public MistakeConflictException(string code) : base(code) => Code = code;
    public string Code { get; }
}

public class MistakeDomainService : IMistakeDomainService
{
    private readonly IMistakeItemRepository _items;
    private readonly IProcessLogRepository _logs;
    private readonly IUnitOfWork _unitOfWork;

    public MistakeDomainService(
        IMistakeItemRepository mistakeItemRepository,
        IProcessLogRepository processLogRepository,
        IUnitOfWork unitOfWork,
        ILogger<MistakeDomainService> logger)
    {
        _items = mistakeItemRepository;
        _logs = processLogRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<MistakeItemModel?> GetMistakeItemAsync(Guid id)
    {
        return await _items.GetByIdAsync(id);
    }

    public Task<bool> IsConversationObjectReferencedAsync(string objectPath, string caller, CancellationToken ct = default) =>
        _items.IsConversationObjectReferencedAsync(objectPath, caller, ct);

    public async Task<List<MistakeItemModel>> GetMistakeItemsByUploadAsync(string sourceUploadId)
    {
        return await _items.GetBySourceUploadIdAsync(sourceUploadId);
    }

    public async Task<(List<MistakeItemModel> Items, int TotalCount)> GetMistakeItemListAsync(
        string studentId, int subject, int grade, int reviewStatus, int page, int size)
    {
        (page, size) = NormalizePage(page, size);

        return await _items.GetListAsync(studentId, subject, grade, reviewStatus, page, size);
    }

    public async Task<MistakeItemModel?> ReviewMistakeItemAsync(Guid id, int reviewStatus, string? reviewerId, string? rootCause, int? grade = null, int? subject = null, string? reviewReason = null)
    {
        var item = await _items.GetByIdAsync(id);
        if (item == null) return null;

        if (item.ReviewStatus != (int)ReviewStatus.PendingReview)
        {
            throw new MistakeValidationException("只能审核待审核状态的错题");
        }

        if (reviewStatus != (int)ReviewStatus.Confirmed && reviewStatus != (int)ReviewStatus.Rejected)
        {
            throw new MistakeValidationException("审核结果只能是确认或退回");
        }

        if (reviewReason?.Length > 2000) throw new MistakeValidationException("审核说明最多2000个字符");

        item.ReviewStatus = reviewStatus;
        item.ReviewerId = reviewerId;
        item.ReviewedAt = DateTimeOffset.UtcNow;
        item.RootCause = rootCause;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        if (grade.HasValue && grade.Value > 0 && grade.Value <= 12)
        {
            item.Grade = grade.Value;
        }

        if (subject.HasValue && subject.Value > 0 && subject.Value <= 9)
        {
            item.Subject = subject.Value;
        }

        await _items.UpdateAsync(item);

        var confirmed = reviewStatus == (int)ReviewStatus.Confirmed;
        var action = confirmed
            ? ProcessAction.ReviewConfirmed
            : ProcessAction.ReviewRejected;
        var detail = confirmed
            ? "Mistake item confirmed"
            : string.IsNullOrWhiteSpace(reviewReason) ? "Mistake item rejected" : "Mistake item rejected: " + reviewReason;

        await AddLogAsync(item.Id, action, detail, item.StudentId);
        await _unitOfWork.SaveChangesAsync();

        return item;
    }

    public async Task<bool> DeleteMistakeItemAsync(Guid id)
    {
        var item = await _items.GetByIdAsync(id);
        if (item == null) return false;

        await _logs.RemoveByMistakeItemIdAsync(id);
        await _items.DeleteAsync(id);
        await _unitOfWork.SaveChangesAsync();

        return true;
    }

    public async Task<MistakeItemModel?> UpdateMistakeItemPartialAsync(Guid id, string? studentId = null, int? subject = null, int? grade = null, List<SourceRegionModel>? sourceRegions = null)
    {
        var item = await _items.GetByIdAsync(id);
        if (item == null) return null;

        if (!string.IsNullOrWhiteSpace(studentId))
            item.StudentId = studentId;
        if (subject.HasValue && subject.Value > 0)
            item.Subject = subject.Value;
        if (grade.HasValue && grade.Value > 0)
            item.Grade = grade.Value;
        if (sourceRegions != null && sourceRegions.Count > 0)
            item.SourceRegions = sourceRegions;

        item.UpdatedAt = DateTimeOffset.UtcNow;

        var result = await _items.UpdateAsync(item);
        if (!result) return null;
        await _unitOfWork.SaveChangesAsync();
        // Re-read so the response carries the source shape the store derived from the saved regions.
        return await _items.GetByIdAsync(id) ?? item;
    }

    public async Task<bool> LogMistakeProcessAsync(Guid mistakeItemId, string studentId, ProcessAction action, string details)
    {
        var item = await _items.GetByIdAsync(mistakeItemId);
        if (item == null) return false;

        await AddLogAsync(mistakeItemId, action, details, studentId);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<List<ProcessLogModel>> GetMistakeProcessHistoryAsync(Guid mistakeItemId)
    {
        return await _logs.GetByMistakeItemIdAsync(mistakeItemId);
    }

    private static (int Page, int Size) NormalizePage(int page, int size) =>
        (Math.Max(1, page), size <= 0 ? 10 : Math.Min(size, 100));

    private async Task AddLogAsync(Guid mistakeItemId, ProcessAction action, string details, string? studentId)
    {
        var processLog = new ProcessLogModel
        {
            Id = Guid.NewGuid(),
            MistakeItemId = mistakeItemId,
            StudentId = studentId,
            Action = (int)action,
            Details = details,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _logs.AddAsync(processLog);
    }
}
