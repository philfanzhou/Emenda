using System;
using System.Collections.Generic;

namespace Ruoyu.Study.Mistake.Domain.Models;

public class MistakeItemModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string SourceUploadId { get; set; } = string.Empty;
    public List<SourceRegionModel> SourceRegions { get; set; } = new();
    public string? QuestionNumber { get; set; }
    public string? QuestionText { get; set; }
    public string? StudentAnswer { get; set; }
    public double AiConfidence { get; set; }
    public int ReviewStatus { get; set; }
    public string? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public int Type { get; set; }
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
