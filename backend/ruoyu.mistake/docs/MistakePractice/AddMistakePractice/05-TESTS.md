# AddMistakePractice — 测试计划 (TESTS)

测试工具：`xUnit + FluentAssertions + Moq`。现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 添加练习成功（验证 SPEC FR-01/FR-04/FR-05/FR-07）

- **Given**：有效的 `MistakePracticeModel { MistakeItemId = itemId, StudentId = "student-1", StudentAnswer = "选A", IsCorrect = false }`；mock `GetByIdAsync` 返回 `ReviewStatus = Confirmed` 的错题条目。
- **When**：调用 `MistakeDomainService.AddPracticeAsync(practice)`。
- **Then**：返回的 `MistakePracticeModel.Id != Guid.Empty`；`MistakeItemId == itemId`；`StudentId == "student-1"`；`StudentAnswer == "选A"`；`IsCorrect == false`；`PracticeDate` 接近当前时间（±5s）；`_mockPracticeRepo.AddAsync` 被调用一次；`_mockUow.SaveChangesAsync` 被调用一次。

### UT-02 添加练习时记录操作日志（验证 SPEC FR-07）

- **Given**：与 UT-01 相同的前置条件。
- **When**：调用 `AddPracticeAsync`。
- **Then**：`_mockLogRepo.AddAsync` 被调用一次，传入的 `ProcessLogModel.Action == (int)ProcessAction.Practiced`，`Details == "Student practiced mistake"`。

### UT-03 错题条目不存在（验证 SPEC FR-02）

- **Given**：`MistakePracticeModel { MistakeItemId = unknownId }`；mock `GetByIdAsync(unknownId)` 返回 null。
- **When**：调用 `AddPracticeAsync`。
- **Then**：抛出 `MistakeValidationException`，消息包含 "错题条目不存在"；`_mockPracticeRepo.AddAsync` 未被调用。

### UT-04 错题条目未确认（验证 SPEC FR-03）

- **Given**：`MistakePracticeModel { MistakeItemId = itemId }`；mock `GetByIdAsync` 返回 `ReviewStatus = PendingReview` 的错题条目。
- **When**：调用 `AddPracticeAsync`。
- **Then**：抛出 `MistakeValidationException`，消息包含 "只能对已确认的错题进行练习"；`_mockPracticeRepo.AddAsync` 未被调用。

### UT-05 错题条目已驳回（验证 SPEC FR-03 扩展）

- **Given**：mock `GetByIdAsync` 返回 `ReviewStatus = Rejected` 的错题条目。
- **When**：调用 `AddPracticeAsync`。
- **Then**：抛出 `MistakeValidationException`，消息包含 "只能对已确认的错题进行练习"。

### UT-06 传入 imagePaths 时覆盖默认值（验证 SPEC FR-06）

- **Given**：有效的练习参数，`imagePaths = ["img1.jpg", "img2.jpg"]`。
- **When**：调用 `AddPracticeAsync(practice, imagePaths)`。
- **Then**：返回的 `practice.ImagePaths.Count == 2`；`ImagePaths[0] == "img1.jpg"`；`ImagePaths[1] == "img2.jpg"`。

### UT-07 不传 imagePaths 时保持空列表（验证 SPEC FR-06）

- **Given**：有效的练习参数，`imagePaths = null`（或不传）。
- **When**：调用 `AddPracticeAsync(practice)`。
- **Then**：返回的 `practice.ImagePaths` 为空列表（`Count == 0`）。

### UT-08 事务一致性 — 保存失败回滚（验证 SPEC FR-08）

- **Given**：有效的创建参数；mock `_unitOfWork.SaveChangesAsync` 抛出异常。
- **When**：调用 `AddPracticeAsync`。
- **Then**：抛出异常；`_practiceRepo.AddAsync` 被调用但数据未持久化；`_logRepo.AddAsync` 被调用但数据未持久化。

## 集成测试

### IT-01 端到端添加 → 查询练习

1. 使用真实的测试数据库上下文。
2. 先创建一条 Confirmed 状态的错题条目。
3. 调用 `AddMistakePractice` gRPC 方法添加练习。
4. 使用 `GetMistakePractices` 查询该错题的练习记录。
5. 断言返回的练习列表中包含刚创建的练习记录，字段与输入一致。

## 边界和异常测试

- EX-01 `student_answer` 为空字符串 → 添加成功（允许空答案）。
- EX-02 `comments` 为 null → 添加成功。
- EX-03 `comments` 为超长文本（如 10000 字符）→ 添加成功（comments 不做长度校验）。
- EX-04 `is_correct = true` → 添加成功（答案正确的练习同样记录）。
- EX-05 `mistake_item_id` 为 `Guid.Empty` → 由 gRPC 层 `Guid.Parse` 或领域层 `GetByIdAsync` 处理。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `AddPracticeAsync_WhenItemConfirmed_ShouldAddPractice` | FR-01, FR-04, FR-05, FR-07 |
| `AddPracticeAsync_WhenItemNotConfirmed_ShouldThrow` | FR-03 |
| gRPC 层添加练习测试（待创建） | FR-09 |
| 事务失败测试（待创建） | FR-08 |