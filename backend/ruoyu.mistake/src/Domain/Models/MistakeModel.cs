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
    public int Type { get; set; }
    public string? QuestionId { get; set; }
    public int Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public class MistakeDetailModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MistakeId { get; set; }
    public List<string> OriginalImages { get; set; } = new();
    public string? Comments { get; set; }
}
