# AnalyzeMistakeType — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：`test/Domain/Services/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 分析错题类型成功（验证 SPEC FR-01/FR-02/FR-03）

- **Given**：存在的错题条目 ID；mock `_mistakeItemRepository.GetByIdAsync` 返回有效 `MistakeItemModel`。
- **When**：调用 `MistakeDomainService.AnalyzeMistakeTypeAsync(mistakeItemId)`。
- **Then**：返回 `MistakeTypeResponse { Type = 5, TypeName = "其他错误", AnalysisReason = "根据答案对比无法明确分类" }`；`_processLogRepository.AddAsync` 被调用一次，`ProcessLogModel.Action == (int)ProcessAction.Analyzed`，`Details` 包含 "Mistake analyzed as 其他错误"；`_unitOfWork.SaveChangesAsync` 被调用一次。

### UT-02 错题条目不存在时不记录日志（验证 SPEC FR-04）

- **Given**：不存在的错题条目 ID；mock `_mistakeItemRepository.GetByIdAsync` 返回 `null`。
- **When**：调用 `MistakeDomainService.AnalyzeMistakeTypeAsync(mistakeItemId)`。
- **Then**：仍返回分析结果 `MistakeTypeResponse`；`_processLogRepository.AddAsync` 不被调用；`_unitOfWork.SaveChangesAsync` 不被调用。

### UT-03 gRPC 层 — 非法 GUID（验证 SPEC FR-05）

- **Given**：`AnalyzeMistakeRequest { MistakeItemId = "not-a-guid" }`。
- **When**：调用 `AnalyzeMistakeType`。
- **Then**：`Guid.Parse` 抛出 `FormatException`。

## 集成测试

### IT-01 端到端分析 → 日志查询

1. 使用真实的测试数据库上下文。
2. 先创建一条错题条目。
3. 调用 `AnalyzeMistakeType` gRPC 方法。
4. 调用 `GetMistakeProcessHistory` 查询该错题的日志。
5. 断言日志列表中包含一条 `Analyzed` 类型的记录。

## 边界和异常测试

- EX-01 `mistake_item_id` 为 `Guid.Empty` → `GetByIdAsync` 返回 null，不记录日志但返回分析结果。
- EX-02 错题条目存在但 `SaveChangesAsync` 抛出异常 → 异常向上传播。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `AnalyzeMistakeType_ShouldReturnHardcodedResult` | FR-01, FR-02 |
| `AnalyzeMistakeType_ShouldLogAnalyzedAction` | FR-03 |
| `AnalyzeMistakeType_ItemNotExists_ShouldNotLog` | FR-04 |
| `AnalyzeMistakeType_InvalidGuid_ShouldThrow` | FR-05 |