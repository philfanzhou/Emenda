# Mistake 与 OSS 集成 — 功能概述

## 概述

Mistake 模块通过 `IOssService` 接口与 OSS（对象存储服务）集成，实现错题图片的存储、迁移和审计功能。系统提供两种 OSS 实现：基于 S3 协议的 `S3OssService`（适用于 MinIO/SeaweedFS）和基于本地文件系统的 `LocalFileOssService`（适用于开发环境）。

## 核心功能

### 1. 图片迁移（uploads/ → mistakes/）

在 `CompleteUploadReview` 流程中，审核通过的错题图片从临时上传路径迁移至正式存储路径：

- **Confirmed item**：将 `uploads/{path}` 复制到 `mistakes/{path}`，然后删除原始 `uploads/{path}`
- **Rejected item**：不迁移图片，原图保留在 uploads 路径下
- **路径映射规则**：`"mistakes" + oldPath.Substring("uploads".Length)`，即仅替换前缀
- **路径缓存**：使用 `pathMapping` 字典避免重复迁移同一路径
- **容错**：单个图片迁移失败不阻塞整体审核完成流程

### 2. 图片引用查询（GetAllReferencedImagePaths）

`MistakeDomainService.GetAllReferencedImagePaths` 返回系统中所有被错题引用的 OSS 路径：

- 来源：`mistake_item.source_regions` 和 `mistake_practice.image_paths`
- 用途：为 OSS 垃圾清理审计提供引用基准数据
- 注意：返回全量数据（无分页），数据量大时可能存在性能问题

### 3. OSS 审计支持

`admin_portal` 的 `OssAuditWorker` 利用 Mistake 模块提供的引用路径进行僵尸对象检测：

- 调用 Mistake 的 `GetAllReferencedImagePaths` 获取错题引用路径
- 调用 Student 的 `GetRegisteredOssPaths` 获取学生模块引用路径
- 交叉比对：不在两者中的 OSS 对象即为僵尸对象，可安全清理

### 4. 两种 OSS 实现模式

| 模式 | 实现 | 适用场景 |
|------|------|----------|
| S3OssService | 基于 S3 协议 | 生产环境（MinIO/SeaweedFS） |
| LocalFileOssService | 基于本地文件系统 | 开发环境 |

通过配置切换，开发环境无需部署 MinIO 即可本地调试。
