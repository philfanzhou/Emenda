# LogMistakeProcess — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：`test/Domain/Services/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 记录处理日志成功（验证 SPEC FR-01/FR-02/FR-03/FR-04/FR-05/FR-06）

- **Given**：存在的错题条目 ID、有效的 `studentId = "student-1"`、`action = ProcessAction.ReviewConfirmed`、`details = "Teacher confirmed mistake"`；mock `_mistakeItemRepository.GetByIdAsync` 返回有效 `MistakeItemModel`。
- **When**：调用 `MistakeDomainService.LogMistakeProcessAsync(mistakeItemId, studentId, action, details)`。
- **Then**：返回 `true`；`_processLogRepository.AddAsync` 被调用一次，传入的 `ProcessLogModel` 满足：`Id != Guid.Empty`、`MistakeItemId` 与入参一致、`Action == (int)ProcessAction.ReviewConfirmed`、`Details == "Teacher confirmed mistake"`、`CreatedAt` 接近当前时间（±5s）；`_unitOfWork.SaveChangesAsync` 被调用一次。

### UT-02 错题条目不存在时返回 false（验证 SPEC FR-07）

- **Given**：不存在的错题条目 ID；mock `_mistakeItemRepository.GetByIdAsync` 返回 `null`。
- **When**：调用 `MistakeDomainService.LogMistakeProcessAsync(mistakeItemId, studentId, action, details)`。
- **Then**：返回 `false`；`_processLogRepository.AddAsync` 不被调用；`_unitOfWork.SaveChangesAsync` 不被调用。

### UT-03 使用 ProcessAction.Created 记录（验证 SPEC FR-04）

- **Given**：存在的错题条目 ID；`action = ProcessAction.Created`。
- **When**：调用 `LogMistakeProcessAsync`。
- **Then**：传入 `AddAsync` 的 `ProcessLogModel.Action == 1`（对应 CREATED）。

### UT-04 使用 ProcessAction.Mastered 记录（验证 SPEC FR-04）

- **Given**：存在的错题条目 ID；`action = ProcessAction.Mastered`。
- **When**：调用 `LogMistakeProcessAsync`。
- **Then**：传入 `AddAsync` 的 `ProcessLogModel.Action == 6`（对应 MASTERED）。

## 集成测试

### IT-01 端到端记录 → 查询

1. 使用真实的测试数据库上下文。
2. 先创建一条错题条目。
3. 调用 `LogMistakeProcess` gRPC 方法记录日志。
4. 调用 `GetMistakeProcessHistory` 查询该错题的日志。
5. 断言日志列表中包含刚创建的记录，字段一致。

## 边界和异常测试

- EX-01 `mistake_item_id` 为 `Guid.Empty` → `GetByIdAsync` 返回 null，返回 `false`。
- EX-02 `details` 为空字符串 → 日志记录 `Details = ""`。
- EX-03 `SaveChangesAsync` 抛出异常 → 异常向上传播。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `LogMistakeProcess_ShouldCreateLog` | FR-01, FR-02, FR-03, FR-04, FR-05, FR-06 |
| `LogMistakeProcess_ItemNotExists_ShouldReturnFalse` | FR-07 |
| `LogMistakeProcess_InvalidGuid_ShouldThrow` | FR-08 |