# SubmitMistakeUpload — 测试计划 (TESTS)

测试工具：`xUnit + FluentAssertions + Moq`。现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 提交错题上传成功（验证 SPEC FR-01/FR-02/FR-03/FR-09）

- **Given**：有效的 student_id="student-1"、subject=1、grade=3、image_paths=["img1.jpg","img2.jpg"]、source_upload_id="upload-1"；mock `GetBySourceUploadIdAsync` 返回空列表。
- **When**：调用 `MistakeDomainService.SubmitMistakeUploadAsync("student-1", 1, 3, imagePaths, null, "upload-1")`。
- **Then**：返回的列表 `Count == 1`；`SourceRegions.Count == 2`；`SourceRegions[0].SourceImagePath == "img1.jpg"`；`SourceRegions[0].BoundingBox == null`；`SourceRegions[1].SourceImagePath == "img2.jpg"`；`ReviewStatus == (int)ReviewStatus.PendingReview`；`_mockUow.SaveChangesAsync` 被调用一次。

### UT-02 操作日志记录（验证 SPEC FR-09）

- **Given**：有效的提交参数。
- **When**：调用 `SubmitMistakeUploadAsync`。
- **Then**：`_mockLogRepo.AddAsync` 被调用一次，传入的 `ProcessLogModel.Action == (int)ProcessAction.Created`，`Details` 包含 "created from upload"。

### UT-03 gRPC 层参数校验 — 空 student_id（验证 SPEC FR-04）

- **Given**：`SubmitMistakeUploadRequest { StudentId = "", Subject = 1, Grade = 3, ImagePaths = ["img.jpg"], SourceUploadId = "upload-1" }`。
- **When**：调用 `SubmitMistakeUpload`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 "缺少学生ID"。

### UT-04 gRPC 层参数校验 — 无效 subject（验证 SPEC FR-05）

- **Given**：`SubmitMistakeUploadRequest { StudentId = "student-1", Subject = 0, Grade = 3, ImagePaths = ["img.jpg"], SourceUploadId = "upload-1" }`。
- **When**：调用 `SubmitMistakeUpload`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 "学科值无效"。

### UT-05 gRPC 层参数校验 — 无效 grade（验证 SPEC FR-06）

- **Given**：`SubmitMistakeUploadRequest { StudentId = "student-1", Subject = 1, Grade = 13, ImagePaths = ["img.jpg"], SourceUploadId = "upload-1" }`。
- **When**：调用 `SubmitMistakeUpload`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 "年级值无效"。

### UT-06 gRPC 层参数校验 — 空 source_upload_id（验证 SPEC FR-07）

- **Given**：`SubmitMistakeUploadRequest { StudentId = "student-1", Subject = 1, Grade = 3, ImagePaths = ["img.jpg"], SourceUploadId = "" }`。
- **When**：调用 `SubmitMistakeUpload`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 "缺少上传记录ID"。

### UT-07 gRPC 层参数校验 — 空 image_paths（验证 SPEC FR-08）

- **Given**：`SubmitMistakeUploadRequest { StudentId = "student-1", Subject = 1, Grade = 3, ImagePaths = [], SourceUploadId = "upload-1" }`。
- **When**：调用 `SubmitMistakeUpload`。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 "至少需要提供一张图片"。

### UT-08 事务一致性 — 保存失败回滚（验证 SPEC FR-10）

- **Given**：有效的创建参数；mock `_unitOfWork.SaveChangesAsync` 抛出异常。
- **When**：调用 `SubmitMistakeUploadAsync`。
- **Then**：抛出异常；`_repository.AddAsync` 被调用但数据未持久化；`_logRepo.AddAsync` 被调用但数据未持久化。

### UT-09 领域层校验 — 聚合所有错误

- **Given**：所有参数均无效（空 student_id、subject=0、grade=0、空 image_paths、空 source_upload_id）。
- **When**：调用 `SubmitMistakeUploadAsync`。
- **Then**：抛出 `MistakeValidationException`，消息中包含 "缺少学生ID"、"学科值无效"、"年级值无效"、"缺少上传记录ID"、"至少需要提供一张图片"（以 `; ` 分隔）。

## 集成测试

### IT-01 端到端提交 → 查询

1. 使用真实的测试数据库上下文。
2. 调用 `SubmitMistakeUpload` gRPC 方法提交错题。
3. 使用 `GetMistakeItem` 查询刚创建的错题。
4. 断言返回的 `MistakeItemDto` 字段与提交时输入一致，`review_status` 为 `REVIEW_STATUS_PENDING_REVIEW`。

## 边界和异常测试

- EX-01 subject 边界值 1 → 提交成功。
- EX-02 subject 边界值 9 → 提交成功。
- EX-03 subject 边界值 10 → gRPC 层返回 InvalidArgument。
- EX-04 grade 边界值 1 → 提交成功。
- EX-05 grade 边界值 12 → 提交成功。
- EX-06 image_paths 包含大量路径（如 100 条）→ 提交成功。
- EX-07 comments 为 null 或超长字符串 → 提交成功（comments 不做长度校验）。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `SubmitMistakeUploadAsync_ShouldCreateItemWithSourceRegions` | FR-01, FR-02, FR-03, FR-09 |
| `SubmitMistakeUploadAsync_WhenAlreadyExists_ShouldReturnExisting` | FR-01（幂等场景） |
| gRPC 层参数校验（待创建） | FR-04, FR-05, FR-06, FR-07, FR-08 |