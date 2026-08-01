# Mistake 与 OSS 集成 — 接口规格

## IOssService 接口

命名空间：`Ruoyu.Study.Common.Oss`

### 关键方法

| 方法 | 签名 | 说明 |
|------|------|------|
| CopyObjectAsync | `Task CopyObjectAsync(string sourceKey, string destKey)` | 将对象从 sourceKey 复制到 destKey |
| DeleteAsync | `Task DeleteAsync(string key)` | 删除指定 key 的对象；自动清理关联缩略图（oss-path-autonomy） |

### 实现

| 实现 | 说明 |
|------|------|
| S3OssService | 基于 S3 协议，适用于 MinIO / SeaweedFS |
| LocalFileOssService | 基于本地文件系统，适用于开发环境 |

## GetAllReferencedImagePaths

### 请求

无参数（返回全量数据，无分页）。

### 响应

返回所有被错题模块引用的 OSS 路径集合。

**数据来源**：

- `mistake_item.source_regions` — 错题原图路径
- `mistake_practice.image_paths` — 练习图片路径

**返回类型**：路径字符串集合（如 `IEnumerable<string>`）

## OSS 路径格式规范

### 错题原图

```
mistakes/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/original_{index}.jpg
```

### 练习图片

```
mistakes/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/practice_{practice_id}_{index}.jpg
```

### 临时上传路径

```
uploads/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/original_{index}.jpg
```

### 迁移路径映射

| 迁移前 | 迁移后 |
|--------|--------|
| `uploads/{rest}` | `mistakes/{rest}` |

映射逻辑：仅替换前缀 `uploads` → `mistakes`，路径其余部分保持不变。

## GetPresignedUrl（oss-path-autonomy 变更）

Mistake 服务恢复直连 OSS，`GetPresignedUrl` 直接调用 `IOssService` + `ThumbnailHelper`。

### 路径前缀校验

仅允许 `mistakes/` 前缀的路径。

### 缩略图路径规则

缩略图与原图同目录，路径格式 `{dirname}/{stem}_{size}.jpg`：

| 原图路径 | 缩略图路径（size=small） |
|----------|--------------------------|
| `mistakes/2025/06/14/def/image.jpg` | `mistakes/2025/06/14/def/image_small.jpg` |

### 缩略图生成逻辑

使用 `ThumbnailHelper.GetOrGenerateThumbnailAsync`，流程：
1. `ThumbnailHelper.GetThumbnailPath(objectPath, size)` 计算缩略图路径
2. 检查缩略图是否已存在（`IOssService.ObjectExistsAsync`）
3. 缓存命中：直接返回缩略图预签名 URL
4. 缓存未命中：下载原图 → SixLabors.ImageSharp 缩放 → 上传缩略图 → 返回预签名 URL
5. 降级策略：缩略图生成失败时返回原图 URL + Warning 日志

### 自动清理

`IOssService.DeleteAsync` 自动清理关联缩略图，无需手动处理。

## 配置参数

### 生产模式（S3OssService）

| 配置键 | 说明 | 示例 |
|--------|------|------|
| Oss:InternalEndpoint | 后端实际连接的 S3 地址（`host:port`） | `10.20.30.40:8333` |
| Oss:InternalSecure | 内部 S3 连接是否使用 HTTPS | `false` |
| Oss:AccessKey | 访问密钥 | `minioadmin` |
| Oss:SecretKey | 密钥 | `minioadmin` |
| Oss:BucketName | 存储桶名称 | `ruoyu-study` |
| Oss:PublicBaseUrl | 公共预签名基础 URL（可选） | `https://ry.zhoufan.asia/oss` |

> `Oss:PublicBaseUrl` 非空时，`GetPresignedUrlAsync` 使用其公共 authority 生成签名，再加入其路径前缀。仓库内由 User Web Nginx 唯一代理 `/oss/`，strip 前缀并保留公共 Host。

### 开发模式（LocalFileOssService）

| 配置键 | 说明 | 默认值 |
|--------|------|--------|
| USE_LOCAL_OSS | 启用本地 OSS（值为 `1`） | — |
| OSS_LOCAL_PATH | 本地存储根路径 | `data/oss` |

### 配置代码位置

`Program.cs` L54-68
