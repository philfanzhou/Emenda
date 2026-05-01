using System;

namespace Ruoyu.Study.Mistake.Domain.Models;

public class ImageFingerprintModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Hash { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public Guid MistakeId { get; set; }
    public int ImageIndex { get; set; }
    public string OssPath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DuplicateImageInfo
{
    public int ImageIndex { get; set; }
    public Guid MistakeId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UploadMistakeResult
{
    public bool Success { get; set; }
    public MistakeModel? Mistake { get; set; }
    public MistakeDetailModel? Detail { get; set; }
    public List<DuplicateImageInfo> Duplicates { get; set; } = new();
}

public class ImageExistenceResult
{
    public int ImageIndex { get; set; }
    public bool Exists { get; set; }
    public Guid MistakeId { get; set; }
    public DateTime CreatedAt { get; set; }
}
