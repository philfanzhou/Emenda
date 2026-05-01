using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ruoyu.Study.Mistake.Database.Entities;

[Table("mistake_image_fingerprint")]
public class ImageFingerprintEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("hash")]
    [Required]
    [MaxLength(64)]
    public string Hash { get; set; } = string.Empty;

    [Column("student_id")]
    [Required]
    [MaxLength(36)]
    public string StudentId { get; set; } = string.Empty;

    [Column("mistake_id")]
    [Required]
    public Guid MistakeId { get; set; }

    [Column("image_index")]
    public int ImageIndex { get; set; }

    [Column("oss_path")]
    [Required]
    public string OssPath { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("MistakeId")]
    public virtual MistakeEntity Mistake { get; set; } = null!;
}
