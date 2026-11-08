using Emenda.Domain.Models;
using Emenda.Domain.Services;

namespace Emenda.Domain.Tests;

/// <summary>
/// IKQA2N geometry-contract tests with known coordinates: normalized-to-pixel mapping and
/// back, EXIF orientation 1-8 rotation and mirroring to the display frame, round-trip
/// mapping without double rotation, already-rotated source pixels, the snapshot's
/// independent rotation metadata, and the layout-result validation rules including the
/// legitimate empty result.
/// </summary>
public sealed class LayoutGeometryTests
{
    private static readonly NormalizedLayoutBox SampleBox = new(0.1, 0.2, 0.3, 0.4);

    [Fact]
    public void NormalizedToPixelMappingUsesStoredFrameAndExactFractions()
    {
        var box = new NormalizedLayoutBox(0.1, 0.2, 0.9, 0.8);
        // Stored frame like the tracker's EXIF-6 smoke image: 1754 x 1240 stored pixels.
        var pixels = LayoutGeometry.ToStoredPixels(box, 1754, 1240);
        Assert.Equal(175.4, pixels.Left, 12);
        Assert.Equal(248.0, pixels.Top, 12);
        Assert.Equal(1578.6, pixels.Right, 12);
        Assert.Equal(992.0, pixels.Bottom, 12);
    }

    [Fact]
    public void PixelToNormalizedRoundTripRecoversTheOriginalBoxExactly()
    {
        var box = new NormalizedLayoutBox(0.037, 0.421, 0.778, 0.953);
        var roundTrip = LayoutGeometry.FromStoredPixels(
            LayoutGeometry.ToStoredPixels(box, 4032, 3024), 4032, 3024);
        Assert.Equal(box.X0, roundTrip.X0, 12);
        Assert.Equal(box.Y0, roundTrip.Y0, 12);
        Assert.Equal(box.X1, roundTrip.X1, 12);
        Assert.Equal(box.Y1, roundTrip.Y1, 12);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]      // Full image stays full under every transform.
    [InlineData(0.1, 0.2, 0.3, 0.4)]
    [InlineData(0.0, 0.0, 0.5, 0.5)] // Top-left quarter, asymmetric under mirroring.
    public void ExifOrientationPointTransformsMatchKnownCoordinates(double x0, double y0, double x1, double y1)
    {
        // Expected display-frame boxes derived by hand for the asymmetric sample box.
        var expected = new Dictionary<int, (double X0, double Y0, double X1, double Y1)>
        {
            [1] = (x0, y0, x1, y1),             // Identity.
            [2] = (1 - x1, y0, 1 - x0, y1),     // Mirror horizontal.
            [3] = (1 - x1, 1 - y1, 1 - x0, 1 - y0), // Rotate 180.
            [4] = (x0, 1 - y1, x1, 1 - y0),     // Mirror vertical.
            [5] = (y0, x0, y1, x1),             // Transpose swaps axes.
            [6] = (1 - y1, x0, 1 - y0, x1),     // Rotate 90 CW.
            [7] = (1 - y1, 1 - x1, 1 - y0, 1 - x0), // Transverse.
            [8] = (y0, 1 - x1, y1, 1 - x0),     // Rotate 90 CCW.
        };
        var box = new NormalizedLayoutBox(x0, y0, x1, y1);
        foreach (var (orientation, exp) in expected)
        {
            var display = LayoutGeometry.ToDisplayFrame(box, orientation);
            Assert.Equal(exp.X0, display.X0, 12);
            Assert.Equal(exp.Y0, display.Y0, 12);
            Assert.Equal(exp.X1, display.X1, 12);
            Assert.Equal(exp.Y1, display.Y1, 12);
        }
    }

    [Fact]
    public void Exif6SmokeImageQuarterMapsToTheRotatedQuadrant()
    {
        // The EXIF-6 smoke image from the upstream verification: stored 1754 x 1240.
        // The stored top-left quarter must land in the display top-right quadrant after
        // the 90-degree clockwise rotation, with the display dimensions swapped.
        var display = LayoutGeometry.ToDisplayFrame(new NormalizedLayoutBox(0, 0, 0.5, 0.5), 6);
        Assert.Equal(0.5, display.X0, 12);
        Assert.Equal(0, display.Y0, 12);
        Assert.Equal(1, display.X1, 12);
        Assert.Equal(0.5, display.Y1, 12);
        Assert.Equal((1240, 1754), LayoutGeometry.DisplayDimensions(1754, 1240, 6));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void DisplayFrameRoundTripNeverAppliesTheRotationTwice(int orientation)
    {
        var roundTrip = LayoutGeometry.FromDisplayFrame(
            LayoutGeometry.ToDisplayFrame(SampleBox, orientation), orientation);
        Assert.Equal(SampleBox.X0, roundTrip.X0, 12);
        Assert.Equal(SampleBox.Y0, roundTrip.Y0, 12);
        Assert.Equal(SampleBox.X1, roundTrip.X1, 12);
        Assert.Equal(SampleBox.Y1, roundTrip.Y1, 12);

        var point = LayoutGeometry.DisplayToStoredPoint(
            orientation, LayoutGeometry.StoredToDisplayPoint(orientation, 0.37, 0.61).U,
            LayoutGeometry.StoredToDisplayPoint(orientation, 0.37, 0.61).V);
        Assert.Equal(0.37, point.U, 12);
        Assert.Equal(0.61, point.V, 12);
    }

    [Fact]
    public void NullOrientationAndAlreadyRotatedPixelsKeepTheStoredFrame()
    {
        // Pixels whose rotation is already baked in (orientation 1 or no tag) map
        // unchanged; the geometry never adds a rotation of its own.
        Assert.Equal(SampleBox, LayoutGeometry.ToDisplayFrame(SampleBox, null));
        Assert.Equal(SampleBox, LayoutGeometry.ToDisplayFrame(SampleBox, 1));
        Assert.Equal((800, 600), LayoutGeometry.DisplayDimensions(800, 600, null));
        Assert.Equal((800, 600), LayoutGeometry.DisplayDimensions(800, 600, 3));
    }

    [Fact]
    public void SnapshotRotationListIsPreservedAsIndependentMetadata()
    {
        // Student's rotation operation rewrites pixels and retains this list unchanged, so
        // it is carried with its original order and length and never composed into the
        // display transform (which reads only the EXIF orientation).
        IReadOnlyList<int> snapshotRotations = [90, 180, 90];
        var preserved = LayoutGeometry.PreserveSnapshotRotations(snapshotRotations);
        Assert.Equal([90, 180, 90], preserved);
        Assert.NotSame(snapshotRotations, preserved);
        Assert.Equal(SampleBox, LayoutGeometry.ToDisplayFrame(SampleBox, null));
    }

    [Fact]
    public void InvalidSnapshotRotationValuesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LayoutGeometry.PreserveSnapshotRotations([90, 45]));
    }

    [Fact]
    public void InvalidOrientationsAndDimensionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutGeometry.ToDisplayFrame(SampleBox, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutGeometry.ToDisplayFrame(SampleBox, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutGeometry.DisplayDimensions(1754, 1240, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutGeometry.ToStoredPixels(SampleBox, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutGeometry.FromStoredPixels(new LayoutPixelRectangle(0, 0, 1, 1), 100, -1));
    }

    [Fact]
    public void ValidResultIncludingLegitimateEmptyRegionsPassesValidation()
    {
        var result = new LayoutDetectionResult
        {
            RequestId = "req-1",
            Width = 1754,
            Height = 1240,
            Orientation = 6,
            Engine = "mineru-cloud",
            Model = "mineru2",
            Version = "1.0",
            Diagnostics = ["note"],
            Regions =
            [
                new LayoutRegion(0, "text", new NormalizedLayoutBox(0, 0, 0.5, 0.5)),
                new LayoutRegion(1, "table", new NormalizedLayoutBox(0.5, 0.5, 1, 1)),
            ],
        };
        Assert.Empty(LayoutGeometry.ValidateLayoutResult(result));

        var empty = result with { Regions = [] };
        Assert.Empty(LayoutGeometry.ValidateLayoutResult(empty));
    }

    [Fact]
    public void BoundaryBoxesAreValidAndDegenerateBoxesTolerated()
    {
        var result = new LayoutDetectionResult
        {
            RequestId = "req-2",
            Width = 10,
            Height = 10,
            Regions =
            [
                new LayoutRegion(0, "figure", new NormalizedLayoutBox(0, 0, 1, 1)),
                new LayoutRegion(1, "text", new NormalizedLayoutBox(0.5, 0.5, 0.5, 0.5)),
            ],
        };
        Assert.Empty(LayoutGeometry.ValidateLayoutResult(result));
    }

    [Fact]
    public void ValidationRejectsEachDocumentedViolation()
    {
        static IReadOnlyList<string> Errors(LayoutDetectionResult result) =>
            LayoutGeometry.ValidateLayoutResult(result);

        Assert.Contains("request id", Assert.Single(Errors(new LayoutDetectionResult
        {
            RequestId = " ",
            Width = 10,
            Height = 10,
            Regions = [],
        })));

        Assert.Contains("dimensions", Assert.Single(Errors(new LayoutDetectionResult
        {
            RequestId = "r",
            Width = 0,
            Height = 10,
            Regions = [],
        })));

        Assert.Contains("orientation", Assert.Single(Errors(new LayoutDetectionResult
        {
            RequestId = "r",
            Width = 10,
            Height = 10,
            Orientation = 9,
            Regions = [],
        })));

        var duplicateIds = new LayoutDetectionResult
        {
            RequestId = "r",
            Width = 10,
            Height = 10,
            Regions =
            [
                new LayoutRegion(0, "text", new NormalizedLayoutBox(0, 0, 0.1, 0.1)),
                new LayoutRegion(0, "text", new NormalizedLayoutBox(0.2, 0, 0.3, 0.1)),
            ],
        };
        Assert.Contains("unique", Assert.Single(Errors(duplicateIds)));

        var outOfBounds = new LayoutDetectionResult
        {
            RequestId = "r",
            Width = 10,
            Height = 10,
            Regions = [new LayoutRegion(0, "text", new NormalizedLayoutBox(0, 0, 1.5, 0.5))],
        };
        Assert.Contains("[0, 1]", Assert.Single(Errors(outOfBounds)));

        var unordered = new LayoutDetectionResult
        {
            RequestId = "r",
            Width = 10,
            Height = 10,
            Regions = [new LayoutRegion(0, "text", new NormalizedLayoutBox(0.8, 0.2, 0.3, 0.9))],
        };
        Assert.Contains("top-left", Assert.Single(Errors(unordered)));

        var nonFinite = new LayoutDetectionResult
        {
            RequestId = "r",
            Width = 10,
            Height = 10,
            Regions = [new LayoutRegion(0, "text", new NormalizedLayoutBox(double.NaN, 0, 0.5, 0.5))],
        };
        Assert.Contains("non-finite", Assert.Single(Errors(nonFinite)));

        var missingLabel = new LayoutDetectionResult
        {
            RequestId = "r",
            Width = 10,
            Height = 10,
            Regions = [new LayoutRegion(0, "", new NormalizedLayoutBox(0, 0, 0.1, 0.1))],
        };
        Assert.Contains("label", Assert.Single(Errors(missingLabel)));

        var emptyMetadata = new LayoutDetectionResult
        {
            RequestId = "r",
            Width = 10,
            Height = 10,
            Engine = " ",
            Regions = [],
        };
        Assert.Contains("engine", Assert.Single(Errors(emptyMetadata)));
    }
}
