# GetMistakePractices — 测试计划 (TESTS)

测试工具：`xUnit + FluentAssertions + Moq`。现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 查询有练习记录时返回完整列表（验证 SPEC FR-01/FR-02）

- **Given**：`mistakeItemId = Guid.NewGuid()`；mock `GetByMistakeItemIdAsync` 返回包含 2 条练习记录的列表，每条记录包含完整的 `StudentId`、`StudentAnswer`、`IsCorrect` 等字段。
- **When**：调用 `MistakeDomainService.GetPracticesAsync(mistakeItemId)`。
- **Then**：返回的列表 `Count == 2`；每条记录的 `MistakeItemId == mistakeItemId`；`_mockPracticeRepo.GetByMistakeItemIdAsync` 被调用一次且参数为 `mistakeItemId`。

### UT-02 查询无练习记录时返回空列表（验证 SPEC FR-03）

- **Given**：`mistakeItemId = Guid.NewGuid()`；mock `GetByMistakeItemIdAsync` 返回空列表。
- **When**：调用 `GetPracticesAsync(mistakeItemId)`。
- **Then**：返回的列表 `Count == 0`（非 null）；不抛出任何异常。

### UT-03 查询操作不产生副作用（验证 SPEC FR-04）

- **Given**：与 UT-01 相同的前置条件。
- **When**：调用 `GetPracticesAsync`。
- **Then**：`_mockUow.SaveChangesAsync` 未被调用；`_mockPracticeRepo.AddAsync` 未被调用；`_mockLogRepo.AddAsync` 未被调用。

### UT-04 gRPC 层返回值转换正确（验证 SPEC FR-05/FR-06）

- **Given**：`IdRequest { Id = mistakeItemId.ToString() }`；mock 领域服务返回 1 条练习记录。
- **When**：调用 `GetMistakePractices` gRPC 方法。
- **Then**：返回的 `MistakePracticeListResponse.Practices.Count == 1`；返回的 `MistakePracticeDto.Id != ""`；`MistakeItemId`、`StudentId`、`PracticeDate`、`StudentAnswer`、`IsCorrect` 字段正确。

### UT-05 PracticeDate 为 MinValue 时返回空字符串

- **Given**：mock 返回的 `MistakePracticeModel.PracticeDate = DateTimeOffset.MinValue`。
- **When**：通过 `MapPracticeDto` 转换。
- **Then**：dto 的 `PracticeDate == ""`（空字符串而非 "0001-01-01T..."）。

## 集成测试

### IT-01 端到端添加 → 查询练习

1. 使用真实的测试数据库上下文。
2. 先通过 `AddMistakePractice` 添加 2 条练习记录。
3. 调用 `GetMistakePractices` gRPC 方法查询。
4. 断言返回的 `Practices` 列表包含 2 条记录，且字段与添加时一致。

## 边界和异常测试

- EX-01 `mistake_item_id` 为 `Guid.Empty` → repository 查询返回空列表，gRPC 返回空 `Practices`。
- EX-02 `mistake_item_id` 对应的错题条目存在但无练习记录 → 返回空列表。
- EX-03 传入非 GUID 格式的 `id`（如 "not-a-guid"）→ `Guid.Parse` 抛出 `FormatException`。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `GetPracticesAsync` 返回列表测试（待创建） | FR-01, FR-02 |
| `GetPracticesAsync` 空列表测试（待创建） | FR-03 |
| 无副作用测试（待创建） | FR-04 |