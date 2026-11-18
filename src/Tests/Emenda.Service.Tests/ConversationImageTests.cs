using Emenda.TestFixtures;
using Emenda.Common.Oss;
using ImageMagick;

namespace Emenda.Service.Tests;

public sealed class ConversationImageTests
{
    [Fact]
    public void QuestionCropsUseDisplayedOrientationAndRoundOutwardsWithoutChangingOriginal()
    {
        using var source = RasterFixture.Create(100, 60);
        source.Orientation = Orientation.RightTop;
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        source.SetProfile(exif);
        var original = source.ToByteArray(MagickFormat.Jpeg);
        using var image = GuardedImageDecoder.LoadQuestion(original);
        var crop = image.CropQuestion(0, 0, 500, 500);
        Assert.Equal(30, crop.Width); Assert.Equal(50, crop.Height);
        Assert.Equal(100, image.Width); Assert.Equal(60, image.Height);
        using var decoded = GuardedImageDecoder.LoadQuestion(crop.Data);
        Assert.Equal(crop.Width, decoded.Width); Assert.Equal(crop.Height, decoded.Height);
        Assert.Throws<ArgumentException>(() => image.CropQuestion(-1, 0, 500, 500));
        Assert.Throws<ArgumentException>(() => image.CropQuestion(500, 0, 500, 1000));
    }

    [Fact]
    public void QuestionDecoderRejectsAnimatedUnsupportedAndExcessivePixelInputs()
    {
        using var frame = RasterFixture.Create(10, 10);
        Assert.Throws<ArgumentException>(() => GuardedImageDecoder.LoadQuestion(frame.ToByteArray(MagickFormat.Gif)));
        Assert.Throws<ArgumentException>(() => GuardedImageDecoder.LoadQuestion(new byte[4_500_001]));
        using var large = RasterFixture.Create(8193, 1);
        Assert.Throws<ArgumentException>(() => GuardedImageDecoder.LoadQuestion(large.ToByteArray(MagickFormat.Png)));
        using var otherFrame = RasterFixture.Create(10, 10, 200, 20, 150);
        using var animated = new MagickImageCollection(); animated.Add(frame.Clone()); animated.Add(otherFrame.Clone());
        var animation = animated.ToByteArray(MagickFormat.WebP);
        using var frames = new MagickImageCollection(animation);
        Assert.Equal(2, frames.Count);
        Assert.Throws<ArgumentException>(() => GuardedImageDecoder.LoadQuestion(animation));
    }
}
