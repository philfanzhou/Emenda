# 完成上传审核 — 测试计划 (TESTS)

测试工具：xUnit + Moq + FluentAssertions。
现有测试文件：
- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`
- `test/Ruoyu.Study.Mistake.Domain.Tests/ImagePathMigrationTests.cs`

## 已有测试

### UT-01: 存在待审核错题时返回错误

- **Given**: Mock `CountPendingBySourceUploadIdAsync("upload-1")` 返回 2
- **When**: 调用 `CompleteUploadReviewAsync("upload-1", "teacher-1")`
- **Then**: `result.Success` 为 `false`，`result.ErrorMessage` 包含 "2 条错题待审核"

### UT-02: 全部审核完成时返回成功

- **Given**: Mock `CountPendingBySourceUploadIdAsync("upload-1")` 返回 0，Mock `GetBySourceUploadIdAsync("upload-1")` 返回 1 条错题（StudentId="student-1"）
- **When**: 调用 `CompleteUploadReviewAsync("upload-1", "teacher-1")`
- **Then**: `result.Success` 为 `true`，`result.SourceUploadId` 为 "upload-1"，`result.StudentId` 为 "student-1"

### UT-03: 无错题记录时返回错误

- **Given**: Mock `CountPendingBySourceUploadIdAsync("upload-1")` 返回 0，Mock `GetBySourceUploadIdAsync("upload-1")` 返回空列表
- **When**: 调用 `CompleteUploadReviewAsync("upload-1", "teacher-1")`
- **Then**: `result.Success` 为 `false`，`result.ErrorMessage` 包含 "未找到该上传记录的错题"

### UT-04: 图片路径替换 uploads/ → mistakes/

- **Given**: SourceRegion 包含 `uploads/student-1/2026/05/03/record/upload_0.jpg`
- **When**: 执行路径迁移
- **Then**: SourceImagePath 变为 `mistakes/student-1/2026/05/03/record/upload_0.jpg`

### UT-05: 相同路径缓存复用

- **Given**: 两个 SourceRegion 共享 `uploads/student-1/2026/05/03/record/upload_0.jpg`
- **When**: 执行路径迁移
- **Then**: 两个 region 的 SourceImagePath 均为 `mistakes/...`，OSS 操作仅 1 次 Copy + 1 次 Delete

### UT-06: 已迁移路径跳过

- **Given**: SourceRegion 包含 `mistakes/student-1/already.jpg`
- **When**: 执行路径迁移
- **Then**: SourceImagePath 不变，无 OSS 操作

### UT-07: 空路径或 null 路径跳过

- **Given**: SourceRegion 包含空字符串、空白字符串、null
- **When**: 执行路径迁移
- **Then**: 不抛异常，路径值不变

### UT-08: 多图片 OSS 操作正确

- **Given**: 4 个 SourceRegion（2 不同 uploads 路径 + 1 重复 + 1 已迁移）
- **When**: 执行路径迁移
- **Then**: 产生 4 个 OSS 操作（2 Copy + 2 Delete），重复路径使用缓存

## 遗漏的测试场景

### UT-09: gRPC 层 Confirmed 错题图片迁移后更新错题记录

- **Given**: Confirmed 错题包含 `uploads/` 前缀图片
- **When**: CompleteUploadReview gRPC handler 执行
- **Then**: 调用 `UpdateMistakeItemAsync` 更新错题记录的 SourceImagePath

### UT-10: gRPC 层 Rejected 错题图片不迁移

- **Given**: Rejected 错题包含 `uploads/` 前缀图片
- **When**: CompleteUploadReview gRPC handler 执行
- **Then**: 不执行 OSS Copy/Delete，图片路径加入 removedImagePaths

### UT-11: OSS Copy 失败时容错

- **Given**: OSS CopyObjectAsync 抛出异常
- **When**: CompleteUploadReview gRPC handler 执行
- **Then**: 记录 Error 日志，继续处理其他图片，最终返回 Success=true

### UT-12: MistakeValidationException 返回 Success=false

- **Given**: Domain Service 抛出 MistakeValidationException
- **When**: CompleteUploadReview gRPC handler 捕获异常
- **Then**: 返回 `CompleteUploadReviewResponse { Success=false, ErrorMessage=ex.Message }`
