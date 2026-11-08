using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emenda.Common.Oss;
using System.Globalization;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Service.Intake;
namespace Emenda.Host.Services.Intake;

public static class IntakeCanonical
{
    public const string Version = "mistake-intake-v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Encode(params string[] parts)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        return Convert.ToBase64String(output.ToArray());
    }
    public static string Hash(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    public static string Payload(int subject, int grade, string? cause)
    {
        if (subject is < 1 or > 9 || grade is < 1 or > 12) throw new SourceIntakeException("invalid_payload", 400);
        return Encode(subject.ToString(System.Globalization.CultureInfo.InvariantCulture), grade.ToString(System.Globalization.CultureInfo.InvariantCulture), cause ?? "");
    }
    public static string Region(SourceRegionModel region)
    {
        if(region==null || string.IsNullOrEmpty(region.SourceImagePath)) throw new SourceIntakeException("invalid_region",400);
        var box = region.BoundingBox;
        if (box != null && (box.X1 < 0 || box.Y1 < 0 || box.X1 >= box.X2 || box.Y1 >= box.Y2))
            throw new SourceIntakeException("invalid_region", 400);
        return Encode(region.SourceImagePath, box == null ? "Full" :
            string.Join(',', new[]{box.X1,box.Y1,box.X2,box.Y2}.Select(value=>value.ToString(CultureInfo.InvariantCulture))));
    }
    public static string Request(Guid source, Guid revision, string student, int subject, int grade, string? cause, List<SourceRegionModel> regions)
    {
        if (source == Guid.Empty || revision == Guid.Empty || regions==null || regions.Count == 0 || string.IsNullOrWhiteSpace(student))
            throw new SourceIntakeException("invalid_request", 400);
        return Encode(Version, source.ToString("D"), revision.ToString("D"), student, Payload(subject, grade, cause),
            Encode(regions.Select(Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
    }
    public static (string Canonical,string[] Atoms,List<SourceRegionModel> Regions) Group(SourceManifest manifest, List<SourceRegionModel> regions)
    {
        var distinct = regions.DistinctBy(Region, StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) throw new SourceIntakeException("invalid_group", 400);
        var atoms = distinct.Select(region =>
        {
            var image = manifest.Images.FirstOrDefault(i => i.Path == region.SourceImagePath && i.Type == "mistake")
                ?? throw new SourceIntakeException("region_not_in_manifest", 400);
            return Encode(Region(region), image.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), image.Sha256);
        }).Order(StringComparer.Ordinal).ToArray();
        return (Encode(Version, manifest.SourceUploadId.ToString("D"), manifest.ContentRevision.ToString("D"), "mistake", Encode(atoms)), atoms, distinct);
    }
    public static void ValidateManifest(SourceManifest value, Guid source, Guid revision, string subject)
    {
        if (value.SourceUploadId != source || value.ContentRevision != revision || value.SnapshotId == Guid.Empty
            || string.IsNullOrWhiteSpace(value.StudentId) || value.StudentId.Length > 36 || value.AcquiredAt == default
            || value.AcquiredBy != subject || value.Images == null || value.ImageRotations == null
            || value.Images.Count == 0 || !value.Images.Any(i => i != null && i.Type == "mistake"))
            throw new SourceIntakeException("invalid_source_manifest", 503);
        foreach (var image in value.Images)
        {
            if (image == null || !ValidPath(image.Path) || string.IsNullOrEmpty(image.Type) || image.Length < 0
                || image.Sha256 == null || image.Sha256.Length != 64 || image.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new SourceIntakeException("invalid_source_manifest", 503);
        }
        if (value.Images.GroupBy(i => i.Path, StringComparer.Ordinal).Any(g => g.Any(i => i.Length != g.First().Length || i.Sha256 != g.First().Sha256)))
            throw new SourceIntakeException("invalid_source_manifest", 503);
    }
    private static bool ValidPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !(path.StartsWith("uploads/", StringComparison.Ordinal) || path.StartsWith("mistakes/", StringComparison.Ordinal))
            || path.StartsWith("uploads/staging/", StringComparison.Ordinal) || path.StartsWith("uploads/homework/", StringComparison.Ordinal)
            || path.StartsWith("uploads/thumbnails/", StringComparison.Ordinal) || path.Contains('\\') || path.Any(char.IsControl)
            || path.Split('/').Any(p => p.Length == 0 || p is "." or "..")
            || path.EndsWith("_thumbnail.jpg", StringComparison.Ordinal) || path.EndsWith("_small.jpg", StringComparison.Ordinal)
            || path.EndsWith("_medium.jpg", StringComparison.Ordinal)) return false;
        for (var i=0;i<path.Length;i++)
        {
            if (char.IsHighSurrogate(path[i])) { if (++i >= path.Length || !char.IsLowSurrogate(path[i])) return false; }
            else if (char.IsLowSurrogate(path[i])) return false;
        }
        return true;
    }
}
