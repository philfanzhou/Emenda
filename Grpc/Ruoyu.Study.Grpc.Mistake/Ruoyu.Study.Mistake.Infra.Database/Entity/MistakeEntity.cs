using Qz.Infra.Database.Entity;
using Ruoyu.Study.Mistake.Infra.Database.Table;

namespace Ruoyu.Study.Mistake.Infra.Database.Entity;

[MapTable(Name = MistakeTable.TableName)]
public class MistakeEntity
{
    [MapColumn(Name = MistakeTable.Id, PrimaryKey = true)]
    public string Id { get; set; } = string.Empty;

    [MapColumn(Name = MistakeTable.StudentId)]
    public string StudentId { get; set; } = string.Empty;

    [MapColumn(Name = MistakeTable.Subject)]
    public int Subject { get; set; }

    [MapColumn(Name = MistakeTable.Grade)]
    public int Grade { get; set; }

    [MapColumn(Name = MistakeTable.Date)]
    public DateTime Date { get; set; }

    [MapColumn(Name = MistakeTable.Image)]
    public byte[]? Image { get; set; }

    [MapColumn(Name = MistakeTable.Thumb)]
    public byte[]? Thumb { get; set; }

    [MapColumn(Name = MistakeTable.Content)]
    public string Content { get; set; } = string.Empty;

    [MapColumn(Name = MistakeTable.CorrectAnswer)]
    public string CorrectAnswer { get; set; } = string.Empty;

    [MapColumn(Name = MistakeTable.StudentAnswer)]
    public string StudentAnswer { get; set; } = string.Empty;

    [MapColumn(Name = MistakeTable.Type)]
    public int Type { get; set; }

    [MapColumn(Name = MistakeTable.LibraryId)]
    public string? LibraryId { get; set; }

    [MapColumn(Name = MistakeTable.Comments)]
    public string Comments { get; set; } = string.Empty;

    [MapColumn(Name = MistakeTable.IsArchived)]
    public bool IsArchived { get; set; }
}
