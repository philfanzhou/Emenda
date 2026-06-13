using System;

namespace Ruoyu.Study.Mistake.Domain.Models;

public class ProcessLogModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MistakeItemId { get; set; } = Guid.Empty;
    public string? StudentId { get; set; }
    public int Action { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
