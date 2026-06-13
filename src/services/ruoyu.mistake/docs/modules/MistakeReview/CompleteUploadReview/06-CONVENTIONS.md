# 完成上传审核 — 约定与规范 (CONVENTIONS)

## 命名约定

- gRPC 方法: `CompleteUploadReview` (PascalCase)
- 请求消息: `CompleteUploadReviewRequest`
- 响应消息: `CompleteUploadReviewResponse`
- 领域服务方法: `CompleteUploadReviewAsync`
- 领域结果模型: `CompleteUploadReviewResult`
- 仓储方法: `CountPendingBySourceUploadIdAsync`, `GetBySourceUploadIdAsync`

## 图片路径迁移规范

- 源路径前缀: `uploads/`
- 目标路径前缀: `mistakes/`
- 迁移规则: `mistakes` + oldPath.Substring(`uploads`.Length)
- 迁移操作: OSS Copy → OSS Delete（顺序执行）
- 去重: 使用 `Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)` 缓存已迁移路径
- 已迁移路径（不以 `uploads/` 开头）跳过迁移

## 日志规范

| 级别 | 场景 | 消息格式 |
|------|------|----------|
| Debug | 图片迁移成功 | `Migrated image from {OldPath} to {NewPath}` |
| Debug | 非 Confirmed 错题跳过迁移 | `Item {ItemId} is not Confirmed (status={Status}), will only remove from upload record, not migrate` |
| Error | OSS 迁移失败 | `Error migrating image {Path}` |

## 安全规范

- reviewerId 由调用方传入，不做服务端鉴权
- sourceUploadId 为外部标识，不做格式校验

## 错误消息格式

| 场景 | 消息文本 | 返回方式 |
|------|----------|----------|
| 存在待审核错题 | `还有 {N} 条错题待审核，请先完成所有审核` | CompleteUploadReviewResponse.Success=false |
| 无错题记录 | `未找到该上传记录的错题` | CompleteUploadReviewResponse.Success=false |
| MistakeValidationException | ex.Message | CompleteUploadReviewResponse.Success=false |
