using System.Text;
using System.Text.Json;
using Emenda.Common.Oss;
using Emenda.Service.Intake;
using ImageMagick;

namespace Emenda.Service.Tests;

public sealed class ExtractionImageTests
{
    [Fact]
    public void DetailedThirtyImageBatch_FitsProviderBudgetWithoutDroppingSources()
    {
        var header = Encoding.ASCII.GetBytes("P6\n1600 1600\n255\n");
        var raster = new byte[header.Length + 1600 * 1600 * 3];
        new Random(31).NextBytes(raster);
        header.CopyTo(raster, 0);
        using var original = new MagickImage(raster);
        var bytes = original.ToByteArray(MagickFormat.Png);
        var budget = ((8 * 1024 * 1024 - 30 * 64 * 1024) / 30) * 3 / 4;
        var preview = QuestionImage.Preview(bytes, budget);
        Assert.True(preview.Length <= budget);
        var inputs = Enumerable.Range(0, 30).Select(_ =>
        {
            var source = Guid.NewGuid();
            return new QuestionAnalysisImage(source, "data:image/jpeg;base64," + Convert.ToBase64String(preview),
                1600, 1600, Enumerable.Repeat(new ExtractionRegion(source, 1.0 / 7, 1.0 / 3, 5.0 / 7, 2.0 / 3), 256).ToList());
        }).ToList();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new QuestionAnalysisRequest(4, inputs), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(payload.Length < 8 * 1024 * 1024, "the complete wire payload, not merely each image, must fit");
        using var decoded = new MagickImage(preview);
        Assert.True(decoded.Width > 0 && decoded.Height > 0);
        Assert.Equal(30, inputs.Select(i => i.SourceId).Distinct().Count());
    }
    [Fact]
    public void RotatedOriginal_CropsInUprightFrame_AndOriginalBytesRemainIntact()
    {
        byte[] pixels = [255,0,0, 0,0,255, 0,255,0, 255,255,0, 0,255,255, 255,0,255];
        var header = Encoding.ASCII.GetBytes("P6\n2 3\n255\n");
        using var source = new MagickImage(header.Concat(pixels).ToArray());
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        source.SetProfile(exif);
        source.Orientation = Orientation.RightTop;
        var original = source.ToByteArray(MagickFormat.Png);
        var copy = original.ToArray();
        var metadata = QuestionImage.Inspect(original, "image/png");
        Assert.Equal(3, metadata.DisplayWidth);
        Assert.Equal(2, metadata.DisplayHeight);
        var cropped = QuestionImage.Crop(original, 0, 0, 1.0 / 3, .5);
        Assert.Equal(1, cropped.Width);
        Assert.Equal(1, cropped.Height);
        using var decoded = new MagickImage(cropped.Bytes);
        using var actualPixels = decoded.GetPixels();
        var color = actualPixels.GetPixel(0, 0).ToColor()!;
        Assert.Equal(0, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(255, color.B);
        Assert.Equal(copy, original);
    }

    [Fact]
    public void RegionValidation_RejectsOutOfBoundsAndNonFiniteCoordinates()
    {
        Assert.Throws<ArgumentException>(() => QuestionImage.Crop([], double.NaN, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => QuestionImage.Crop([], 0, 0, 2, 1));
        Assert.Throws<ArgumentException>(() => QuestionImage.Crop([], .5, 0, .5, 1));
    }
}
