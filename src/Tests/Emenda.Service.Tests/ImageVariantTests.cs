using Emenda.Common.Oss;
using Emenda.TestFixtures;
using ImageMagick;

namespace Emenda.Service.Tests;

public sealed class ImageVariantTests
{
    [Theory]
    [InlineData(null, "full")]
    [InlineData("", "full")]
    [InlineData("FULL", "full")]
    [InlineData("large", "full")]
    [InlineData("Medium", "medium")]
    [InlineData("small", "small")]
    [InlineData(" thumbnail ", "thumbnail")]
    [InlineData("huge", null)]
    [InlineData("../x", null)]
    [InlineData("small/../../x", null)]
    public void SizeVocabularyIsClosed(string? requested, string? expected) => Assert.Equal(expected, ImageVariants.Normalize(requested));

    [Fact]
    public void RenderIsUprightBoundedAndNeverEnlarges()
    {
        using var tall = RasterFixture.Create(1000, 3000);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6); // stored 1000 x 3000, displayed as 3000 x 1000
        tall.SetProfile(exif);
        tall.Orientation = Orientation.RightTop;
        var photo = tall.ToByteArray(MagickFormat.Png);
        using (var check = new MagickImage(photo)) Assert.Equal(Orientation.RightTop, check.Orientation);
        var rendered = ImageVariants.Render(photo);
        Assert.Equal(["medium", "small", "thumbnail"], rendered.Keys.ToArray());
        foreach (var (size, max) in ImageVariants.Sizes)
        {
            Assert.True(rendered[size].AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }));
            using var decoded = new MagickImage(rendered[size]);
            Assert.Equal((uint)max, decoded.Width);
            Assert.InRange(decoded.Height, (uint)(max / 3), (uint)(max / 3) + 1);
            Assert.Equal(Orientation.Undefined, decoded.Orientation);
        }

        using var small = RasterFixture.Create(150, 90);
        small.Alpha(AlphaAction.Transparent);
        foreach (var bytes in ImageVariants.Render(small.ToByteArray(MagickFormat.Png)).Values)
        {
            using var decoded = new MagickImage(bytes);
            Assert.Equal(150u, decoded.Width);
            Assert.Equal(90u, decoded.Height);
            // Transparency is flattened on white, not black.
            Assert.True(decoded.GetPixels().GetPixel(10, 10).ToColor()!.R > 240);
        }
    }

    [Fact]
    public async Task VariantsAreStoredBesideTheParentAndRemovedWhenTheParentDisappears()
    {
        var root = Path.Combine(Path.GetTempPath(), "emenda-variants-" + Guid.NewGuid().ToString("N"));
        try
        {
            var oss = new LocalFileOssService(root);
            using var image = RasterFixture.Create(900, 600);
            var bytes = image.ToByteArray(MagickFormat.Png);
            var parent = await oss.UploadAsync(bytes, "page.png", "image/png", OssBucket.Mistakes, "legacy/2026");
            (bool stored, Dictionary<string, byte[]> variants) = await ImageVariants.StoreAsync(oss, parent, bytes);
            Assert.True(stored);
            Assert.True(await ImageVariants.AllExistAsync(oss, parent));
            Assert.Equal(variants["small"], await ImageVariants.TryReadAsync(oss, parent, "small"));
            Assert.Equal("mistakes/legacy/2026/page_small.jpg", ImageVariants.PathFor(parent, "small"));

            // A deleter removed the parent before this writer finished: the writer cleans up after itself.
            var gone = "mistakes/legacy/2026/deleted.png";
            var kept = (await ImageVariants.StoreAsync(oss, gone, bytes)).Stored;
            Assert.False(kept);
            foreach (var (size, _) in ImageVariants.Sizes) Assert.False(await oss.ObjectExistsAsync(ImageVariants.PathFor(gone, size)));

            // A read renders on a cache miss and never needs the parent again afterwards.
            await oss.DeleteAsync(ImageVariants.PathFor(parent, "thumbnail"));
            var calls = 0;
            var thumbnail = await ImageVariants.ReadOrCreateAsync(oss, parent, "thumbnail", _ => { calls++; return Task.FromResult(bytes); });
            Assert.Equal(1, calls);
            Assert.Equal(thumbnail, await ImageVariants.ReadOrCreateAsync(oss, parent, "thumbnail", _ => { calls++; return Task.FromResult(bytes); }));
            Assert.Equal(1, calls);
            await Assert.ThrowsAsync<ArgumentException>(() => ImageVariants.ReadOrCreateAsync(oss, parent, "large", _ => Task.FromResult(bytes)));
            await Assert.ThrowsAsync<ArgumentException>(() => ThumbnailHelper.GetOrGenerateThumbnailAsync(parent, "huge", oss));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
