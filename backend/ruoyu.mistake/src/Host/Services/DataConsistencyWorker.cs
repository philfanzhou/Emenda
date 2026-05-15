using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Student.Contract.Protos;

namespace Ruoyu.Study.Mistake.Host.Services;

public class DataConsistencyWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DataConsistencyWorker> _logger;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public DataConsistencyWorker(
        IServiceProvider serviceProvider,
        ILogger<DataConsistencyWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DiagnoseAsync(stoppingToken);
                await RepairAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据一致性修复执行失败");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task DiagnoseAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();

        var items = await dbContext.MistakeItems
            .Select(m => new
            {
                m.Id,
                m.StudentId,
                m.Subject,
                m.Grade,
                m.SourceUploadId,
                m.ReviewStatus,
                m.SourceRegions,
                m.CreatedAt
            })
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            _logger.LogInformation("数据诊断：mistake_item 表为空，无错题记录");
            return;
        }

        _logger.LogInformation("数据诊断：mistake_item 表共 {Count} 条记录", items.Count);

        foreach (var item in items)
        {
            _logger.LogInformation(
                "  MistakeItem: Id={Id}, StudentId={StudentId}, Subject={Subject}, Grade={Grade}, " +
                "SourceUploadId={SourceUploadId}, ReviewStatus={ReviewStatus}, " +
                "SourceRegionsCount={SourceRegionsCount}, CreatedAt={CreatedAt}",
                item.Id, item.StudentId, item.Subject, item.Grade,
                item.SourceUploadId, item.ReviewStatus,
                item.SourceRegions?.Count ?? 0, item.CreatedAt);

            if (item.SourceRegions != null)
            {
                foreach (var region in item.SourceRegions)
                {
                    _logger.LogInformation("    Region: SourceImagePath={Path}, HasBoundingBox={HasBbox}",
                        region.SourceImagePath, region.BoundingBox != null);
                }
            }
        }

        var invalidStatus = items.Where(i => i.ReviewStatus < 1 || i.ReviewStatus > 3).ToList();
        if (invalidStatus.Count > 0)
        {
            _logger.LogWarning("数据诊断：发现 {Count} 条 ReviewStatus 值异常（不在1-3范围内）的记录", invalidStatus.Count);
            foreach (var item in invalidStatus)
            {
                _logger.LogWarning("  异常记录: Id={Id}, ReviewStatus={ReviewStatus}", item.Id, item.ReviewStatus);
            }
        }

        var emptyUploadId = items.Where(i => string.IsNullOrWhiteSpace(i.SourceUploadId)).ToList();
        if (emptyUploadId.Count > 0)
        {
            _logger.LogWarning("数据诊断：发现 {Count} 条 SourceUploadId 为空的记录", emptyUploadId.Count);
        }
    }

    private async Task RepairAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMistakeItemRepository>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
        var studentClient = scope.ServiceProvider
            .GetRequiredService<StudentLearningGrpcService.StudentLearningGrpcServiceClient>();

        var repairedStatus = await FixInvalidReviewStatusAsync(dbContext);
        var summaries = await repository.GetSourceUploadSummariesAsync();
        if (summaries.Count == 0) return;

        _logger.LogInformation("数据一致性检查：发现 {Count} 条错题上传记录，开始修复", summaries.Count);

        int repairedClassification = 0;
        int cleanedUp = 0;

        foreach (var summary in summaries)
        {
            try
            {
                var markRequest = new MarkUploadRecordCompletedRequest
                {
                    RecordId = summary.SourceUploadId,
                    StudentId = summary.StudentId,
                    Classification = 2,
                };

                var markResponse = await studentClient.MarkUploadRecordCompletedAsync(markRequest, cancellationToken: cancellationToken);
                if (markResponse.Success)
                {
                    repairedClassification++;
                    _logger.LogInformation("已修复上传记录 {UploadId} 的 Classification 为 Mistake", summary.SourceUploadId);
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                _logger.LogDebug("上传记录 {UploadId} 已不存在，跳过", summary.SourceUploadId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "修复上传记录 {UploadId} Classification 失败", summary.SourceUploadId);
            }

            if (!summary.HasPendingReview && summary.HasConfirmedOrRejected)
            {
                try
                {
                    var deleteRequest = new DeleteUploadRecordRequest
                    {
                        RecordId = summary.SourceUploadId,
                        StudentId = summary.StudentId,
                    };

                    var deleteResponse = await studentClient.DeleteUploadRecordAsync(deleteRequest, cancellationToken: cancellationToken);
                    if (deleteResponse.Success)
                    {
                        cleanedUp++;
                        _logger.LogInformation("已清理已审核完成的上传记录 {UploadId}", summary.SourceUploadId);
                    }
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
                {
                    _logger.LogDebug("上传记录 {UploadId} 已不存在，跳过清理", summary.SourceUploadId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理上传记录 {UploadId} 失败", summary.SourceUploadId);
                }
            }
        }

        if (repairedClassification > 0 || cleanedUp > 0 || repairedStatus > 0)
        {
            _logger.LogInformation(
                "数据一致性修复完成：修复 ReviewStatus {Status} 条，修复 Classification {Repaired} 条，清理上传记录 {Cleaned} 条",
                repairedStatus, repairedClassification, cleanedUp);
        }
        else
        {
            _logger.LogInformation("数据一致性检查完成：无需要修复的数据");
        }
    }

    private async Task<int> FixInvalidReviewStatusAsync(MistakeDbContext dbContext)
    {
        var invalidItems = await dbContext.MistakeItems
            .Where(m => m.ReviewStatus < 1 || m.ReviewStatus > 3)
            .ToListAsync();

        if (invalidItems.Count == 0) return 0;

        _logger.LogWarning("发现 {Count} 条 ReviewStatus 异常的记录，将修复为 PendingReview(1)", invalidItems.Count);

        foreach (var item in invalidItems)
        {
            _logger.LogWarning("修复 MistakeItem {Id}: ReviewStatus 从 {OldStatus} 改为 1", item.Id, item.ReviewStatus);
            item.ReviewStatus = 1;
        }

        await dbContext.SaveChangesAsync();
        return invalidItems.Count;
    }
}
