using System;

namespace Ruoyu.Study.Mistake.Domain.Models;

public class ProcessLogModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MistakeItemId { get; set; } = Guid.Empty;
    public int Action { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
