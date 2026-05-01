using System;
using System.Collections.Generic;

namespace Ruoyu.Study.Mistake.Domain.Models;

public class MistakePracticeModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MistakeId { get; set; } = Guid.Empty;
    public string StudentId { get; set; } = string.Empty;
    public DateTime PracticeDate { get; set; } = DateTime.UtcNow;
    public List<string> ImagePaths { get; set; } = new();
    public string? StudentAnswer { get; set; }
    public bool IsCorrect { get; set; }
    public string? Comments { get; set; }
}
