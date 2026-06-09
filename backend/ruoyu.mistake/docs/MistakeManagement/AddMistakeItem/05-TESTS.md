# AddMistakeItem — 测试计划 (TESTS)

测试工具：`xUnit + Moq + FluentAssertions`。现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 创建错题条目成功（验证 SPEC FR-01/FR-07/FR-08/FR-09/FR-10）

- **Given**：有效的 MistakeItemModel，包含 StudentId="student-1"、Subject=1、Grade=3、SourceUploadId="upload-1"、两个 SourceRegion（一个有 BoundingBox(x1=10,y1=20,x2=100,y2=200)、一个 BoundingBox=null）。
- **When**：调用 `MistakeDomainService.AddMistakeItemAsync(item)`。
- **Then**：返回的 `MistakeItemModel.Id != Guid.Empty`；`StudentId == "student-1"`；`Subject == 1`；`Grade == 3`；`SourceUploadId == "upload-1"`；`SourceRegions.Count == 2`；`SourceRegions[0].BoundingBox` 不为 null 且坐标为 (10,20,100,200)；`SourceRegions[1].BoundingBox` 为 null；`ReviewStatus == (int)ReviewStatus.PendingReview`；`CreatedAt` 接近当前时间（±5s）。

### UT-02 操作日志记录验证（验证 SPEC FR-08）

- **Given**：有效的 MistakeItemModel。
- **When**：调用 `AddMistakeItemAsync`。
- **Then**：`mockProcessLogRepo.AddAsync` 被调用一次，传入的 ProcessLogModel.Action == (int)ProcessAction.Created，Details == "Mistake item manually added by teacher"。

### UT-03 事务提交验证（验证 SPEC FR-11）

- **Given**：有效的 MistakeItemModel。
- **When**：调用 `AddMistakeItemAsync`。
- **Then**：`Mock<IUnitOfWork>.SaveChangesAsync` 被调用一次。

### UT-04 参数校验 — 空 student_id（验证 SPEC FR-02）

- **Given**：`MistakeItemModel { StudentId = "", Subject = 1, Grade = 3, SourceUploadId = "upload-1", SourceRegions = [1个] }`。
- **When**：调用 `AddMistakeItemAsync`。
- **Then**：抛出 `MistakeValidationException`，消息包含 "缺少学生ID"。

### UT-05 参数校验 — 无效 subject（验证 SPEC FR-03）

- **Given**：`MistakeItemModel { StudentId = "s1", Subject = -1, Grade = 3, SourceUploadId = "upload-1", SourceRegions = [1个] }`。
- **When**：调用 `AddMistakeItemAsync`。
- **Then**：抛出 `MistakeValidationException`，消息包含 "学科值无效"。

### UT-06 参数校验 — 无效 grade（验证 SPEC FR-04）

- **Given**：`MistakeItemModel { StudentId = "s1", Subject = 1, Grade = 13, SourceUploadId = "upload-1", SourceRegions = [1个] }`。
- **When**：调用 `AddMistakeItemAsync`。
- **Then**：抛出 `MistakeValidationException`，消息包含 "年级值无效"。

### UT-07 参数校验 — 空 source_upload_id（验证 SPEC FR-05）

- **Given**：`MistakeItemModel { StudentId = "s1", Subject = 1, Grade = 3, SourceUploadId = "", SourceRegions = [1个] }`。
- **When**：调用 `AddMistakeItemAsync`。
- **Then**：抛出 `MistakeValidationException`，消息包含 "缺少上传记录ID"。

### UT-08 参数校验 — 空 source_regions（验证 SPEC FR-06）

- **Given**：`MistakeItemModel { StudentId = "s1", Subject = 1, Grade = 3, SourceUploadId = "upload-1", SourceRegions = [] }`。
- **When**：调用 `AddMistakeItemAsync`。
- **Then**：抛出 `MistakeValidationException`，消息包含 "至少需要一个图片区域"。

### UT-09 事务一致性 — 保存失败回滚（验证 SPEC FR-11）

- **Given**：有效的创建参数；mock `_unitOfWork.SaveChangesAsync` 抛出异常。
- **When**：调用 `AddMistakeItemAsync`。
- **Then**：异常向上传播；`_repository.AddAsync` 和 `_logRepo.AddAsync` 被调用但数据未持久化。

## 集成测试

### IT-01 端到端 gRPC 创建 → 查询

1. 使用真实的测试数据库上下文。
2. 调用 `AddMistakeItem` gRPC 方法创建错题条目。
3. 使用 `GetMistakeItem` 查询刚创建的条目。
4. 断言返回的 `MistakeItemDto` 字段与创建时输入一致，review_status 为 REVIEW_STATUS_PENDING_REVIEW。

## 边界和异常测试

- EX-01 subject 恰好为 0 → 创建成功。
- EX-02 subject 恰好为 9 → 创建成功。
- EX-03 grade 恰好为 0 → 创建成功。
- EX-04 grade 恰好为 12 → 创建成功。
- EX-05 SourceRegion 不包含 BoundingBox → 创建成功，BoundingBox 为 null。
- EX-06 SourceRegion 包含 BoundingBox 且坐标为负值 → 应创建成功（坐标值不在此层校验）。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `AddMistakeItemAsync_WithSourceRegions_ShouldSucceed` | FR-01, FR-07, FR-08, FR-09, FR-10, FR-11 |