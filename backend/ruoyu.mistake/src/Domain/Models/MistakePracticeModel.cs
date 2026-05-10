using System;
using System.Collections.Generic;

namespace Ruoyu.Study.Mistake.Domain.Models;

public class MistakePracticeModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MistakeItemId { get; set; } = Guid.Empty;
    public string StudentId { get; set; } = string.Empty;
    public DateTimeOffset PracticeDate { get; set; } = DateTimeOffset.UtcNow;
    public List<string> ImagePaths { get; set; } = new();
    public string? StudentAnswer { get; set; }
    public bool IsCorrect { get; set; }
    public string? Comments { get; set; }
}
