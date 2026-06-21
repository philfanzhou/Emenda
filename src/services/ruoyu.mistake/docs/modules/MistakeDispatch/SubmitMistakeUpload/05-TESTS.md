# SubmitMistakeUpload - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`
- `test/Ruoyu.Study.Mistake.Service.Tests/UploadPollingWorkerTests.cs`

## 单元测试

### SubmitMistakeUploadAsync_ShouldCreateItemWithSourceRegions

- **Given** 有效的学生ID("student-1")、学科(1)、年级(3)、图片路径列表(["img1.jpg", "img2.jpg"])和上传记录ID("upload-1")
- **When** 调用 SubmitMistakeUploadAsync
- **Then** 返回列表包含 1 个 MistakeItemModel，SourceRegions 包含 2 个元素，第一个 SourceImagePath 为 "img1.jpg" 且 BoundingBox 为 null，第二个 SourceImagePath 为 "img2.jpg"，ReviewStatus 为 PendingReview，SaveChangesAsync 被调用一次

### SubmitMistakeUploadAsync_WhenAlreadyExists_ShouldCreateNewItem

- **Given** sourceUploadId="upload-1" 已存在一个 MistakeItemModel
- **When** 再次调用 SubmitMistakeUploadAsync（相同 sourceUploadId）
- **Then** 返回新的 MistakeItemModel（ID 不同于已有条目），SourceUploadId 为 "upload-1"

## 遗漏的测试场景

| 场景 | 说明 | 优先级 |
|------|------|--------|
| studentId 为空时应抛出 MistakeValidationException | 验证必填字段 | 高 |
| subject 超出范围(0 或 10)时应抛出 MistakeValidationException | 验证学科范围 | 高 |
| grade 超出范围(0 或 13)时应抛出 MistakeValidationException | 验证年级范围 | 高 |
| sourceUploadId 为空时应抛出 MistakeValidationException | 验证必填字段 | 高 |
| imagePaths 为空列表时应抛出 MistakeValidationException | 验证至少一张图片 | 高 |
| 多个验证错误同时存在时错误消息应合并 | 验证错误合并逻辑 | 中 |
| ProcessLog 记录验证 | 验证 ProcessLog 的 Action 和 details 是否正确 | 中 |
| comments 字段传递 | 验证 comments 可选字段正确传递 | 低 |

### UploadPollingWorker 幂等测试

| 场景 | 说明 | 优先级 |
|------|------|--------|
| 全部图片已处理时应跳过 VL/OSS，直接通知删除 | 存量脏数据自动清理 | 高 |
| 部分图片已处理时应只处理未处理图片 | 部分失败后重试 | 高 |
| 无已处理图片时应正常处理全部图片 | 首次处理，幂等检查不影响正常流程 | 高 |
| 已处理图片路径包含 mistakes/ 前缀时应正确匹配 | OSS 迁移后路径已更新 | 中 |
