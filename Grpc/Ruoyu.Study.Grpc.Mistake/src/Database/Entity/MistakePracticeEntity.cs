using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entity;

[Table("mistake_practice")]
public class MistakePracticeEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("mistake_id")]
    [Required]
    public string MistakeId { get; set; } = string.Empty;

    [Column("student_id")]
    [Required]
    public string StudentId { get; set; } = string.Empty;

    [Column("practice_date")]
    public DateTime PracticeDate { get; set; } = DateTime.UtcNow;

    [Column("image_paths")]
    public string? ImagePaths { get; set; }

    [Column("student_answer")]
    public string? StudentAnswer { get; set; }

    [Column("is_correct")]
    public bool IsCorrect { get; set; }

    [Column("comments")]
    public string? Comments { get; set; }

    [ForeignKey("MistakeId")]
    public virtual MistakeEntity Mistake { get; set; } = null!;

    // Helper property for domain service - EF Core uses HasConversion for storage
    [NotMapped]
    public List<string> ImagePathList
    {
        get => string.IsNullOrEmpty(ImagePaths)
            ? new List<string>()
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(ImagePaths) ?? new List<string>();
        set => ImagePaths = System.Text.Json.JsonSerializer.Serialize(value);
    }
}
