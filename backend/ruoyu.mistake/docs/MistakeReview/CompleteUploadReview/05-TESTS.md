# CompleteUploadReview — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：待创建。

## 单元测试 — Given-When-Then 格式

### UT-01 领域服务 — 存在待审核错题拒绝完成（验证 SPEC FR-02）

- **Given**：Mock `CountPendingBySourceUploadIdAsync` 返回 3。
- **When**：调用 `MistakeDomainService.CompleteUploadReviewAsync("upload-1", "reviewer-1")`。
- **Then**：返回 `CompleteUploadReviewResult { Success = false, ErrorMessage = "还有 3 条错题待审核，请先完成所有审核" }`。

### UT-02 领域服务 — 上传记录不存在（验证 SPEC FR-03）

- **Given**：Mock `CountPendingBySourceUploadIdAsync` 返回 0；Mock `GetBySourceUploadIdAsync` 返回空列表。
- **When**：调用 `CompleteUploadReviewAsync("nonexistent", "reviewer-1")`。
- **Then**：返回 `CompleteUploadReviewResult { Success = false, ErrorMessage = "未找到该上传记录的错题" }`。

### UT-03 领域服务 — 校验通过返回数据和 StudentId（验证 SPEC FR-01）

- **Given**：Mock pendingCount=0；Mock 返回 2 条错题（StudentId="student-1"）。
- **When**：调用 `CompleteUploadReviewAsync("upload-1", "reviewer-1")`。
- **Then**：返回 `Success = true`，`Items.Count == 2`，`StudentId == "student-1"`。

### UT-04 gRPC 层 — 已确认错题 OSS 迁移（验证 SPEC FR-04）

- **Given**：领域服务返回 `Success=true`、一条 Confirmed 错题，`SourceRegions = [{ SourceImagePath = "uploads/2025/img.png" }]`；Mock `IOssService` 正常执行。
- **When**：调用 gRPC `CompleteUploadReview`。
- **Then**：`_ossService.CopyObjectAsync("uploads/2025/img.png", "mistakes/2025/img.png")` 被调用；`_ossService.DeleteAsync("uploads/2025/img.png")` 被调用；`_mistakeService.UpdateMistakeItemAsync` 被调用，传入模型的 `SourceRegions[0].SourceImagePath == "mistakes/2025/img.png"`。

### UT-05 gRPC 层 — 已驳回错题跳过迁移（验证 SPEC FR-05）

- **Given**：领域服务返回一条 Rejected 错题，`SourceRegions = [{ SourceImagePath = "uploads/rejected.png" }]`。
- **When**：调用 gRPC `CompleteUploadReview`。
- **Then**：`_ossService.CopyObjectAsync` 和 `_ossService.DeleteAsync` 均未被调用；路径保持 `uploads/rejected.png` 不变；`_logger.LogInformation` 记录 "Item {ItemId} is not Confirmed...not migrate"。

### UT-06 gRPC 层 — 路径映射缓存（验证 SPEC FR-06）

- **Given**：领域服务返回 2 条 Confirmed 错题，均引用 `uploads/shared.png`。
- **When**：调用 gRPC `CompleteUploadReview`。
- **Then**：`_ossService.CopyObjectAsync` 仅调用 1 次；`_ossService.DeleteAsync` 仅调用 1 次；两条错题的 `SourceRegions[0].SourceImagePath` 均更新为 `mistakes/shared.png`。

### UT-07 gRPC 层 — 非 uploads/ 前缀跳过（验证 SPEC FR-07）

- **Given**：领域服务返回一条 Confirmed 错题，`SourceRegions = [{ SourceImagePath = "other/not-an-upload.png" }]`。
- **When**：调用 gRPC `CompleteUploadReview`。
- **Then**：`_ossService.CopyObjectAsync` 和 `DeleteAsync` 均未被调用；路径保持 `other/not-an-upload.png`。

### UT-08 gRPC 层 — OSS 拷贝失败容错（验证 SPEC FR-08）

- **Given**：Mock `IOssService.CopyObjectAsync` 对第一条图片抛 `Exception`，第二条图片正常。
- **When**：调用 gRPC `CompleteUploadReview`。
- **Then**：`_logger.LogError` 被调用 1 次；第二条图片的 `CopyObjectAsync` 和 `DeleteAsync` 正常执行；最终返回 `Success = true`。

### UT-09 gRPC 层 — 调用 Student 服务移除图片（验证 SPEC FR-10）

- **Given**：领域服务返回 1 条 Confirmed 错题，`SourceRegions = [{ SourceImagePath = "uploads/img.png" }]`，`StudentId = "student-1"`。
- **When**：调用 gRPC `CompleteUploadReview`。
- **Then**：`_studentClient.RemoveImagesFromRecordAsync` 被调用 1 次；`RecordId == "upload-1"`；`StudentId == "student-1"`；`ImagePaths` 包含 `"uploads/img.png"`。

### UT-10 gRPC 层 — Student 服务调用失败容错（验证 SPEC FR-11）

- **Given**：Mock `StudentLearningGrpcServiceClient.RemoveImagesFromRecordAsync` 抛出 `RpcException`。
- **When**：调用 gRPC `CompleteUploadReview`。
- **Then**：`_logger.LogError` 被调用；最终返回 `Success = true`。

## 集成测试

### IT-01 端到端审核完成流程

1. 使用真实的测试数据库上下文和测试 OSS 实例。
2. 创建上传记录，插入 3 条错题（2 条 Confirmed、1 条 Rejected）。
3. 调用 `CompleteUploadReview` gRPC 方法。
4. 断言返回 `Success = true`。
5. 断言 OSS 中 `uploads/` 路径文件被删除，`mistakes/` 路径文件存在。
6. 断言数据库中 Confirmed 错题的 `source_regions` 路径已更新。
7. 断言 Student 服务 `RemoveImagesFromRecord` 被调用。

## 边界和异常测试

- EX-01 所有错题均为 Rejected → OSS 无迁移操作，仅清理上传记录图片引用。
- EX-02 source_upload_id 为空字符串 → 领域服务应校验或返回错误。
- EX-03 错题无 SourceRegions（空列表）→ 正常完成，无 OSS 操作。
- EX-04 同一上传记录有多个 Confirmed 错题引用不同图片 → 每张图片独立迁移。
- EX-05 新增：`source_upload_id` 的图片路径同时存在于多处 → `pathMapping` 确保只迁移一次。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `CompleteUploadReviewAsync_HasPending` | FR-02 |
| `CompleteUploadReviewAsync_NoItems` | FR-03 |
| `CompleteUploadReviewAsync_ValidReturn` | FR-01 |
| `CompleteUploadReview_MigrateConfirmed` | FR-04, FR-09 |
| `CompleteUploadReview_SkipRejected` | FR-05 |
| `CompleteUploadReview_PathMappingCache` | FR-06 |
| `CompleteUploadReview_NonUploadsPath` | FR-07 |
| `CompleteUploadReview_OssFailureGraceful` | FR-08 |
| `CompleteUploadReview_CallsRemoveImagesFromRecord` | FR-10 |
| `CompleteUploadReview_StudentServiceFailure` | FR-11 |