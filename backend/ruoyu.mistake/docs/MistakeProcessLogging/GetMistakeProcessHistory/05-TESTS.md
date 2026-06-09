# GetMistakeProcessHistory — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：`test/Domain/Services/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 查询处理日志历史成功（验证 SPEC FR-01/FR-02/FR-03）

- **Given**：存在的错题条目 ID；mock `_processLogRepository.GetByMistakeItemIdAsync` 返回包含 3 条 `ProcessLogModel` 的列表（分别为 CREATED、ANALYZED、REVIEW_CONFIRMED 操作）。
- **When**：调用 `MistakeDomainService.GetMistakeProcessHistoryAsync(mistakeItemId)`。
- **Then**：返回的 `List<ProcessLogModel>` 包含 3 条记录；`_processLogRepository.GetByMistakeItemIdAsync` 被调用一次，传入的 ID 与入参一致。

### UT-02 空日志列表（验证 SPEC FR-04）

- **Given**：无对应日志的错题条目 ID；mock `_processLogRepository.GetByMistakeItemIdAsync` 返回空列表。
- **When**：调用 `MistakeDomainService.GetMistakeProcessHistoryAsync(mistakeItemId)`。
- **Then**：返回空 `List<ProcessLogModel>`。

### UT-03 gRPC 层 — 非法 GUID（验证 SPEC FR-05）

- **Given**：`IdRequest { Id = "not-a-guid" }`。
- **When**：调用 `GetMistakeProcessHistory`。
- **Then**：`Guid.Parse` 抛出 `FormatException`。

## 集成测试

### IT-01 端到端记录多步 → 查询完整历史

1. 使用真实的测试数据库上下文。
2. 先创建一条错题条目。
3. 依次调用 `LogMistakeProcess` 记录 CREATED、ANALYZED、REVIEW_CONFIRMED 三条日志。
4. 调用 `GetMistakeProcessHistory` gRPC 方法查询该错题的日志。
5. 断言日志列表包含 3 条记录，按 `created_at` 升序排列，且各条 `action` 与记录时一致。

## 边界和异常测试

- EX-01 `mistake_item_id` 为 `Guid.Empty` → 仓储正常查询，返回空列表或有效列表。
- EX-02 仓储返回 `null`（不应发生）→ 若仓储实现返回 null 可能导致 `Select(MapProcessLogDto)` 空引用。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `GetMistakeProcessHistory_ShouldReturnLogs` | FR-01, FR-02, FR-03 |
| `GetMistakeProcessHistory_Empty_ShouldReturnEmpty` | FR-04 |
| `GetMistakeProcessHistory_InvalidGuid_ShouldThrow` | FR-05 |