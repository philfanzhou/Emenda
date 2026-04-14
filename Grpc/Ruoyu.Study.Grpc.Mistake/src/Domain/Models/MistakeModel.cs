using System;
using System.Collections.Generic;

namespace Ruoyu.Study.Mistake.Domain.Models;

public class MistakeModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public DateTime Date { get; set; }
    public List<string> ImagePaths { get; set; } = new();
    public string? Content { get; set; }
    public string? CorrectAnswer { get; set; }
    public string? StudentAnswer { get; set; }
    public int Type { get; set; }
    public string? QuestionId { get; set; }
    public string? Comments { get; set; }
    public int Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
