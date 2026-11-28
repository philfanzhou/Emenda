using Microsoft.Extensions.Logging;

namespace Emenda.Common.Oss;

/// <summary>
/// 缩略图路径推导 + 生成 + 删除的共享逻辑。
/// 路径规则：与原图同目录，文件名格式为 {stem}_{size}.jpg
/// 此规则一旦上线不可变更，否则会产生孤儿文件。
/// </summary>
public static class ThumbnailHelper
{
    /// <summary>支持的缩略图尺寸及最大像素（唯一定义见 <see cref="ImageVariants.Sizes"/>）</summary>
    public static readonly IReadOnlyDictionary<string, int> SizeMap =
        ImageVariants.Sizes.ToDictionary(s => s.Size, s => s.MaxPixel);

    /// <summary>
    /// 推导缩略图路径。规则：同目录 + _{size} 后缀 + .jpg 扩展名
    /// 示例：uploads/2025/06/14/abc/image.jpg + small → uploads/2025/06/14/abc/image_small.jpg
    /// </summary>
    public static string GetThumbnailPath(string objectPath, string size)
    {
        var dir = Path.GetDirectoryName(objectPath)?.Replace("\\", "/") ?? "";
        var stem = Path.GetFileNameWithoutExtension(objectPath);
        var fileName = $"{stem}_{size}.jpg";
        return string.IsNullOrEmpty(dir) ? fileName : $"{dir}/{fileName}";
    }

    /// <summary>
    /// 返回原图对应的所有尺寸缩略图路径
    /// </summary>
    public static List<string> GetAllThumbnailPaths(string objectPath)
    {
        return SizeMap.Keys.Select(size => GetThumbnailPath(objectPath, size)).ToList();
    }

    /// <summary>
    /// 判断路径是否是缩略图路径（匹配 _{size}.jpg 模式）
    /// </summary>
    public static bool IsThumbnailPath(string objectPath)
    {
        if (!objectPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(objectPath);
        foreach (var size in SizeMap.Keys)
        {
            if (fileName.EndsWith($"_{size}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 检查缓存→生成（一次解码生成全部尺寸）→返回预签名 URL。size 必须是 <see cref="ImageVariants.Sizes"/> 之一。
    /// </summary>
    public static async Task<(string url, int expirySeconds)> GetOrGenerateThumbnailAsync(
        string objectPath,
        string size,
        IOssService ossService,
        int expirySeconds = 3600,
        ILogger? logger = null)
    {
        if (ImageVariants.Normalize(size) is not { } normalized || normalized == ImageVariants.Full)
            throw new ArgumentException("Unsupported thumbnail size.", nameof(size));
        var thumbnailPath = GetThumbnailPath(objectPath, normalized);

        // 检查缓存
        if (await ossService.ObjectExistsAsync(thumbnailPath))
        {
            var cachedUrl = await ossService.GetPresignedUrlAsync(thumbnailPath, expirySeconds);
            return (cachedUrl, expirySeconds);
        }

        // 生成缩略图
        try
        {
            byte[] original;
            await using (var originalStream = await ossService.DownloadAsync(objectPath))
            using (var memory = new MemoryStream())
            {
                await originalStream.CopyToAsync(memory);
                original = memory.ToArray();
            }
            var (stored, _) = await ImageVariants.StoreAsync(ossService, objectPath, original);
            if (!stored) throw new FileNotFoundException("Original image was removed while generating its thumbnail.");

            var thumbnailUrl = await ossService.GetPresignedUrlAsync(thumbnailPath, expirySeconds);
            return (thumbnailUrl, expirySeconds);
        }
        catch (Exception ex)
        {
            // 缩略图生成失败：降级返回原图
            logger?.LogWarning(ex, "Thumbnail generation failed for {Path}, size={Size}, falling back to original", objectPath, normalized);
            var fallbackUrl = await ossService.GetPresignedUrlAsync(objectPath, expirySeconds);
            return (fallbackUrl, expirySeconds);
        }
    }

    /// <summary>
    /// 删除原图 + 所有尺寸缩略图
    /// </summary>
    public static async Task DeleteWithThumbnailsAsync(string objectPath, IOssService ossService)
    {
        // 先删原图再删缩略图：并发生成者在上传后复查原图，因此不会留下孤儿缩略图。
        await ossService.DeleteAsync(objectPath);
        foreach (var thumbPath in GetAllThumbnailPaths(objectPath))
        {
            await ossService.DeleteAsync(thumbPath);
        }
    }
}
