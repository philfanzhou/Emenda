using FluentAssertions;
using Xunit;

namespace Ruoyu.Study.Mistake.Service.Tests;

/// <summary>
/// 图片迁移路径计算 UT：纯逻辑测试，不需要 mock。
/// 路径替换逻辑来自 StudentLearningServiceImpl.MigrateImagesToMistake：
///   - sourcePath 必须以 "uploads/" 开头（不区分大小写）
///   - newPath = "mistakes" + sourcePath.Substring("uploads".Length)
///   - 不以 "uploads/" 开头的路径不替换，返回错误
/// </summary>
public class ImagePathMappingTests
{
    /// <summary>
    /// 模拟 MigrateImagesToMistake 中的路径替换逻辑。
    /// </summary>
    private static (string newPath, bool success, string? errorMessage) ComputeMigratedPath(string sourcePath)
    {
        if (!sourcePath.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
        {
            return (sourcePath, false, "Source path must start with uploads/");
        }

        var newPath = "mistakes" + sourcePath.Substring("uploads".Length);
        return (newPath, true, null);
    }

    // ============================================================
    // uploads/ → mistakes/ 路径替换
    // ============================================================

    [Fact]
    public void MigratePath_StandardPath_ReplacesUploadsWithMistakes()
    {
        var (newPath, success, _) = ComputeMigratedPath("uploads/2025/06/abc.jpg");

        success.Should().BeTrue();
        newPath.Should().Be("mistakes/2025/06/abc.jpg");
    }

    [Fact]
    public void MigratePath_DeepNestedPath_ReplacesCorrectly()
    {
        var (newPath, success, _) = ComputeMigratedPath("uploads/student-1/2025/06/record-id/upload_0.jpg");

        success.Should().BeTrue();
        newPath.Should().Be("mistakes/student-1/2025/06/record-id/upload_0.jpg");
    }

    [Fact]
    public void MigratePath_UploadsCaseInsensitive_UpperCaseAlsoWorks()
    {
        var (newPath, success, _) = ComputeMigratedPath("Uploads/2025/06/abc.jpg");

        success.Should().BeTrue();
        newPath.Should().Be("mistakes/2025/06/abc.jpg");
    }

    // ============================================================
    // 路径去重
    // ============================================================

    [Fact]
    public void MigratePaths_DuplicatePaths_ShouldProduceSameNewPath()
    {
        var paths = new[] { "uploads/2025/06/abc.jpg", "uploads/2025/06/abc.jpg" };

        var results = paths.Select(ComputeMigratedPath).ToList();

        results.Should().AllSatisfy(r => r.success.Should().BeTrue());
        results[0].newPath.Should().Be(results[1].newPath);
        results[0].newPath.Should().Be("mistakes/2025/06/abc.jpg");
    }

    [Fact]
    public void MigratePaths_DistinctPaths_ShouldProduceDistinctNewPaths()
    {
        var paths = new[] { "uploads/2025/06/abc.jpg", "uploads/2025/06/def.jpg" };

        var results = paths.Select(ComputeMigratedPath).ToList();

        results.Should().AllSatisfy(r => r.success.Should().BeTrue());
        results[0].newPath.Should().NotBe(results[1].newPath);
    }

    // ============================================================
    // 特殊字符处理
    // ============================================================

    [Fact]
    public void MigratePath_PathWithSpaces_ReplacesCorrectly()
    {
        var (newPath, success, _) = ComputeMigratedPath("uploads/2025/06/my image.jpg");

        success.Should().BeTrue();
        newPath.Should().Be("mistakes/2025/06/my image.jpg");
    }

    [Fact]
    public void MigratePath_PathWithChineseCharacters_ReplacesCorrectly()
    {
        var (newPath, success, _) = ComputeMigratedPath("uploads/2025/06/图片.jpg");

        success.Should().BeTrue();
        newPath.Should().Be("mistakes/2025/06/图片.jpg");
    }

    [Fact]
    public void MigratePath_PathWithUrlEncodedChars_ReplacesCorrectly()
    {
        var (newPath, success, _) = ComputeMigratedPath("uploads/2025/06/file%20name.jpg");

        success.Should().BeTrue();
        newPath.Should().Be("mistakes/2025/06/file%20name.jpg");
    }

    [Fact]
    public void MigratePath_PathWithParentheses_ReplacesCorrectly()
    {
        var (newPath, success, _) = ComputeMigratedPath("uploads/2025/06/photo(1).jpg");

        success.Should().BeTrue();
        newPath.Should().Be("mistakes/2025/06/photo(1).jpg");
    }

    // ============================================================
    // 非 uploads/ 前缀路径不替换
    // ============================================================

    [Fact]
    public void MigratePath_MistakesPrefixPath_ShouldReturnError()
    {
        var (newPath, success, errorMessage) = ComputeMigratedPath("mistakes/2025/06/abc.jpg");

        success.Should().BeFalse();
        newPath.Should().Be("mistakes/2025/06/abc.jpg"); // 原路径不变
        errorMessage.Should().Contain("uploads/");
    }

    [Fact]
    public void MigratePath_StagingPrefixPath_ShouldReturnError()
    {
        var (newPath, success, errorMessage) = ComputeMigratedPath("staging/abc123/upload.jpg");

        success.Should().BeFalse();
        errorMessage.Should().Contain("uploads/");
    }

    [Fact]
    public void MigratePath_AbsolutePath_ShouldReturnError()
    {
        var (newPath, success, errorMessage) = ComputeMigratedPath("/var/data/uploads/2025/06/abc.jpg");

        success.Should().BeFalse();
        errorMessage.Should().Contain("uploads/");
    }

    [Fact]
    public void MigratePath_EmptyPath_ShouldReturnError()
    {
        var (newPath, success, errorMessage) = ComputeMigratedPath("");

        success.Should().BeFalse();
        errorMessage.Should().Contain("uploads/");
    }

    [Fact]
    public void MigratePath_OnlyUploadsPrefix_NoSlash_ShouldReturnError()
    {
        // "uploads" 没有 "/" 后缀，不匹配 "uploads/" 前缀
        var (newPath, success, errorMessage) = ComputeMigratedPath("uploads");

        success.Should().BeFalse();
        errorMessage.Should().Contain("uploads/");
    }

    [Fact]
    public void MigratePath_UploadsPrefixWithSlash_ShouldSucceed()
    {
        var (newPath, success, _) = ComputeMigratedPath("uploads/");

        success.Should().BeTrue();
        newPath.Should().Be("mistakes/");
    }

    // ============================================================
    // 混合路径批量处理
    // ============================================================

    [Fact]
    public void MigratePaths_MixedValidAndInvalidPaths_ShouldHandleEachIndependently()
    {
        var paths = new[]
        {
            "uploads/2025/06/abc.jpg",
            "mistakes/2025/06/already.jpg",
            "uploads/2025/06/def.jpg",
            "staging/test.jpg"
        };

        var results = paths.Select(ComputeMigratedPath).ToList();

        results[0].success.Should().BeTrue();
        results[0].newPath.Should().Be("mistakes/2025/06/abc.jpg");

        results[1].success.Should().BeFalse();
        results[1].newPath.Should().Be("mistakes/2025/06/already.jpg");

        results[2].success.Should().BeTrue();
        results[2].newPath.Should().Be("mistakes/2025/06/def.jpg");

        results[3].success.Should().BeFalse();
    }
}
