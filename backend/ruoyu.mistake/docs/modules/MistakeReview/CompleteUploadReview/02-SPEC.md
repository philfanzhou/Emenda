# 完成上传审核 — 详细需求规格 (SPEC)

> ⚠️ **此功能已废弃。** 目标流程中不存在独立的 CompleteUploadReview 步骤，每条审核通过时立即迁移。仅保留供 LegacyClean 运维兜底使用。

## 功能概述

完成一次上传记录的全部审核流程。当某次上传记录下的所有错题均已完成审核（Confirmed 或 Rejected）后，教师可调用此接口完成审核。系统将已确认错题的图片从 `uploads/` 迁移至 `mistakes/`，并返回所有需从上传记录中移除的图片路径。

## 功能要求清单

- [ ] FR-01: 响应 `CompleteUploadReview` gRPC 请求，返回 `CompleteUploadReviewResponse`
- [ ] FR-02: 检查该 sourceUploadId 下是否存在 PendingReview 状态的错题，存在则返回错误
- [ ] FR-03: 若该 sourceUploadId 下无任何错题记录，返回错误"未找到该上传记录的错题"
- [ ] FR-04: 全部审核完成后，返回 `Success=true`、`studentId`、`removedImagePaths`
- [ ] FR-05: 对 Confirmed 状态错题的图片执行 OSS 迁移：`uploads/` → `mistakes/`（Copy + Delete）
- [ ] FR-06: 非 Confirmed 状态错题的图片仅加入 `removedImagePaths`，不执行 OSS 迁移
- [ ] FR-07: 同一图片路径在多个错题中引用时，仅执行一次 OSS Copy+Delete（pathMapping 缓存）
- [ ] FR-08: OSS 迁移失败时记录日志但不中断流程
- [ ] FR-09: MistakeValidationException 时返回 `Success=false`，包含 `ErrorMessage`

## 验收标准

### AC-FR-02: 存在待审核错题

- **Given**: sourceUploadId="upload-1" 下有 2 条 PendingReview 状态的错题
- **When**: 调用 `CompleteUploadReview(sourceUploadId="upload-1", reviewerId="teacher-1")`
- **Then**: 返回 `Success=false`，ErrorMessage 包含 "2 条错题待审核"

### AC-FR-03: 无错题记录

- **Given**: sourceUploadId="upload-1" 下无任何错题记录
- **When**: 调用 `CompleteUploadReview(sourceUploadId="upload-1", reviewerId="teacher-1")`
- **Then**: 返回 `Success=false`，ErrorMessage 为 "未找到该上传记录的错题"

### AC-FR-04: 全部审核完成

- **Given**: sourceUploadId="upload-1" 下有 3 条错题（2 Confirmed + 1 Rejected），studentId="student-1"
- **When**: 调用 `CompleteUploadReview(sourceUploadId="upload-1", reviewerId="teacher-1")`
- **Then**: 返回 `Success=true`，`studentId="student-1"`，`removedImagePaths` 包含所有图片路径

### AC-FR-05: Confirmed 错题图片迁移

- **Given**: Confirmed 错题的 SourceRegion 包含 `uploads/student-1/img1.jpg`
- **When**: 完成 CompleteUploadReview
- **Then**: 执行 OSS Copy `uploads/student-1/img1.jpg` → `mistakes/student-1/img1.jpg`，然后 Delete 原路径，错题记录的 SourceImagePath 更新为 `mistakes/student-1/img1.jpg`

### AC-FR-06: 非 Confirmed 错题图片不迁移

- **Given**: Rejected 错题的 SourceRegion 包含 `uploads/student-1/img2.jpg`
- **When**: 完成 CompleteUploadReview
- **Then**: `img2.jpg` 加入 `removedImagePaths`，但不执行 OSS Copy/Delete，错题记录的 SourceImagePath 不变

### AC-FR-07: 图片路径去重

- **Given**: 两条 Confirmed 错题共享同一图片 `uploads/student-1/img1.jpg`
- **When**: 完成 CompleteUploadReview
- **Then**: 仅执行一次 OSS Copy+Delete，第二条错题使用缓存的新路径

## 非功能需求

- **可靠性**: OSS 迁移失败不阻塞流程，记录 Error 日志后继续
- **一致性**: 迁移后更新错题记录的 SourceImagePath 并持久化

## 测试策略

- 单元测试覆盖 Domain Service 的审核完成逻辑（待审核存在、无记录、全部审核完成）
- 单元测试覆盖图片路径迁移逻辑（路径替换、缓存复用、空路径跳过、已迁移路径跳过）
- 集成测试验证 gRPC 层的 OSS 操作和错题更新
