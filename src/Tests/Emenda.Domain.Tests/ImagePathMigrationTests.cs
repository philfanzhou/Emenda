using System;
using System.Collections.Generic;
using FluentAssertions;
using Emenda.Domain.Models;
using Xunit;

namespace Emenda.Domain.Tests;

public class ImagePathMigrationTests
{
    private record OssOperation(string SourcePath, string DestPath, string Type);

    private static (List<SourceRegionModel> Regions, List<OssOperation> OssOps) MigratePathsWithOssTracking(List<SourceRegionModel> regions)
    {
        var pathMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ossOps = new List<OssOperation>();

        foreach (var region in regions)
        {
            if (string.IsNullOrWhiteSpace(region.SourceImagePath))
                continue;

            if (!region.SourceImagePath.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (pathMapping.TryGetValue(region.SourceImagePath, out var cachedNewPath))
            {
                region.SourceImagePath = cachedNewPath;
                continue;
            }

            var oldPath = region.SourceImagePath;
            var newPath = "mistakes" + oldPath.Substring("uploads".Length);
            ossOps.Add(new OssOperation(oldPath, newPath, "Copy"));
            ossOps.Add(new OssOperation(oldPath, "", "Delete"));
            pathMapping[oldPath] = newPath;
            region.SourceImagePath = newPath;
        }

        return (regions, ossOps);
    }

    private static List<SourceRegionModel> MigratePaths(List<SourceRegionModel> regions)
    {
        var pathMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var region in regions)
        {
            if (string.IsNullOrWhiteSpace(region.SourceImagePath))
                continue;

            if (!region.SourceImagePath.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (pathMapping.TryGetValue(region.SourceImagePath, out var cachedNewPath))
            {
                region.SourceImagePath = cachedNewPath;
                continue;
            }

            var oldPath = region.SourceImagePath;
            var newPath = "mistakes" + oldPath.Substring("uploads".Length);
            pathMapping[oldPath] = newPath;
            region.SourceImagePath = newPath;
        }

        return regions;
    }

    [Fact]
    public void MigratePath_ShouldReplaceUploadsWithMistakes()
    {
        var regions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = "uploads/student-1/2026/05/03/record/upload_0.jpg" }
        };

        var result = MigratePaths(regions);

        result[0].SourceImagePath.Should().Be("mistakes/student-1/2026/05/03/record/upload_0.jpg");
    }

    [Fact]
    public void MigratePath_WithCache_ShouldReuseMigratedPath()
    {
        var sharedPath = "uploads/student-1/2026/05/03/record/upload_0.jpg";
        var regions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = sharedPath },
            new() { SourceImagePath = sharedPath }
        };

        var result = MigratePaths(regions);

        result[0].SourceImagePath.Should().Be("mistakes/student-1/2026/05/03/record/upload_0.jpg");
        result[1].SourceImagePath.Should().Be("mistakes/student-1/2026/05/03/record/upload_0.jpg");
    }

    [Fact]
    public void MigratePath_WithAlreadyMistakesPath_ShouldSkip()
    {
        var regions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = "mistakes/student-1/2026/05/03/record/upload_0.jpg" }
        };

        var result = MigratePaths(regions);

        result[0].SourceImagePath.Should().Be("mistakes/student-1/2026/05/03/record/upload_0.jpg");
    }

    [Fact]
    public void MigratePath_WithEmptyOrNullPath_ShouldSkip()
    {
        var regions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = "" },
            new() { SourceImagePath = "   " },
            new() { SourceImagePath = null! }
        };

        var act = () => MigratePaths(regions);

        act.Should().NotThrow();
        regions[0].SourceImagePath.Should().Be("");
        regions[1].SourceImagePath.Should().Be("   ");
        regions[2].SourceImagePath.Should().BeNull();
    }

    [Fact]
    public void MigratePath_WithMultipleImages_ShouldIssueCorrectOssOperations()
    {
        var regions = new List<SourceRegionModel>
        {
            new() { SourceImagePath = "uploads/student-1/img1.jpg" },
            new() { SourceImagePath = "uploads/student-1/img2.jpg" },
            new() { SourceImagePath = "uploads/student-1/img1.jpg" },
            new() { SourceImagePath = "mistakes/student-1/already.jpg" }
        };

        var (resultRegions, ossOps) = MigratePathsWithOssTracking(regions);

        resultRegions[0].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
        resultRegions[1].SourceImagePath.Should().Be("mistakes/student-1/img2.jpg");
        resultRegions[2].SourceImagePath.Should().Be("mistakes/student-1/img1.jpg");
        resultRegions[3].SourceImagePath.Should().Be("mistakes/student-1/already.jpg");

        ossOps.Should().HaveCount(4);
        ossOps[0].Should().Be(new OssOperation("uploads/student-1/img1.jpg", "mistakes/student-1/img1.jpg", "Copy"));
        ossOps[1].Should().Be(new OssOperation("uploads/student-1/img1.jpg", "", "Delete"));
        ossOps[2].Should().Be(new OssOperation("uploads/student-1/img2.jpg", "mistakes/student-1/img2.jpg", "Copy"));
        ossOps[3].Should().Be(new OssOperation("uploads/student-1/img2.jpg", "", "Delete"));
    }
}
