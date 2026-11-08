using System;
using System.Collections.Generic;

namespace Emenda.Domain.Models;

public class MistakeItemModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string SourceUploadId { get; set; } = string.Empty;
    public List<SourceRegionModel> SourceRegions { get; set; } = new();
    public int ReviewStatus { get; set; }
    public string? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RootCause { get; set; }
    // 注意：历史上存在 Type（int 1-6 枚举，对应"错误类型"）字段，
    // 因无法覆盖全部错题场景已于 2026 年 6 月下线。
    public string? QuestionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class SourceRegionModel
{
    public string SourceImagePath { get; set; } = string.Empty;
    public BoundingBoxModel? BoundingBox { get; set; }
}

public class BoundingBoxModel
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
}

public class PendingReviewUploadModel
{
    public string SourceUploadId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public int PendingCount { get; set; }
    public int TotalCount { get; set; }
    public DateTimeOffset EarliestCreatedAt { get; set; }
}
