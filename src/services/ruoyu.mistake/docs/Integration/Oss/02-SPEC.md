# Mistake 与 OSS 集成 — 接口规格

## IOssService 接口

命名空间：`Ruoyu.Study.Common.Oss`

### 关键方法

| 方法 | 签名 | 说明 |
|------|------|------|
| CopyObjectAsync | `Task CopyObjectAsync(string sourceKey, string destKey)` | 将对象从 sourceKey 复制到 destKey |
| DeleteAsync | `Task DeleteAsync(string key)` | 删除指定 key 的对象 |

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

## 配置参数

### 生产模式（S3OssService）

| 配置键 | 说明 | 示例 |
|--------|------|------|
| Oss:Endpoint | S3 服务端点 | `http://minio:9000` |
| Oss:AccessKey | 访问密钥 | `minioadmin` |
| Oss:SecretKey | 密钥 | `minioadmin` |
| Oss:BucketName | 存储桶名称 | `ruoyu-study` |

### 开发模式（LocalFileOssService）

| 配置键 | 说明 | 默认值 |
|--------|------|--------|
| USE_LOCAL_OSS | 启用本地 OSS（值为 `1`） | — |
| OSS_LOCAL_PATH | 本地存储根路径 | `data/oss` |

### 配置代码位置

`Program.cs` L54-68
