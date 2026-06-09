# UpdateMistakeItem — 测试计划 (TESTS)

测试工具：`xUnit + Moq + FluentAssertions`。现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 领域服务更新成功（验证 SPEC FR-07）

- **Given**：有效的 `MistakeItemModel`（Id=某个 Guid，StudentId="student-1"）；mock `_mockItemRepo.UpdateAsync(item)` 返回 true。
- **When**：调用 `MistakeDomainService.UpdateMistakeItemAsync(item)`。
- **Then**：返回 `true`；`_mockUow.SaveChangesAsync` 被调用一次。

### UT-02 gRPC 层部分更新 — 仅更新 student_id（验证 SPEC FR-02）

- **Given**：`UpdateMistakeItemRequest { Id = "guid", StudentId = "new-student", Subject = 0, Grade = 0, SourceRegions = [] }`；mock `GetByIdAsync` 返回原条目（StudentId="old-student"）。
- **When**：调用 `UpdateMistakeItem`。
- **Then**：更新后条目 `StudentId == "new-student"`，其他字段保持原值。

### UT-03 gRPC 层部分更新 — 仅更新 subject（验证 SPEC FR-03）

- **Given**：`UpdateMistakeItemRequest { Id = "guid", StudentId = "", Subject = 5, Grade = 0, SourceRegions = [] }`；mock `GetByIdAsync` 返回原条目（Subject=3）。
- **When**：调用 `UpdateMistakeItem`。
- **Then**：更新后条目 `Subject == 5`，其他字段保持原值。

### UT-04 gRPC 层部分更新 — 仅更新 grade（验证 SPEC FR-04）

- **Given**：`UpdateMistakeItemRequest { Id = "guid", StudentId = "", Subject = 0, Grade = 8, SourceRegions = [] }`；mock `GetByIdAsync` 返回原条目（Grade=3）。
- **When**：调用 `UpdateMistakeItem`。
- **Then**：更新后条目 `Grade == 8`，其他字段保持原值。

### UT-05 gRPC 层部分更新 — 仅更新 source_regions（验证 SPEC FR-05/FR-06）

- **Given**：`UpdateMistakeItemRequest { Id = "guid", SourceRegions = [new SourceRegion { SourceImagePath = "new.jpg", BoundingBox = new BoundingBox { X1=10, Y1=20, X2=100, Y2=200 } }] }`；mock `GetByIdAsync` 返回原条目（SourceRegions 含 "old.jpg"）。
- **When**：调用 `UpdateMistakeItem`。
- **Then**：更新后条目 `SourceRegions.Count == 1`，`SourceRegions[0].SourceImagePath == "new.jpg"`，BoundingBox 坐标正确。

### UT-06 gRPC 层条目不存在（验证 SPEC FR-01）

- **Given**：`UpdateMistakeItemRequest` 传入不存在的 ID；mock `GetByIdAsync` 返回 null。
- **When**：调用 `UpdateMistakeItem`。
- **Then**：抛出 `RpcException`，`StatusCode == NotFound`，消息为 "错题条目不存在"。

### UT-07 UpdateAsync 返回 false（验证 SPEC FR-09）

- **Given**：有效的条目 ID；mock `UpdateAsync` 返回 false。
- **When**：调用 `UpdateMistakeItem`。
- **Then**：抛出 `RpcException`，`StatusCode == Internal`，消息为 "更新失败"。

### UT-08 更新不触发操作日志（验证 SPEC FR-08）

- **Given**：有效的条目和 mock 设置。
- **When**：调用 `UpdateMistakeItemAsync`。
- **Then**：`Mock<IProcessLogRepository>.AddAsync` 未被调用。

## 集成测试

### IT-01 端到端 gRPC 更新 → 查询

1. 使用真实的测试数据库上下文。
2. 先调用 `AddMistakeItem` 创建一条错题条目。
3. 调用 `UpdateMistakeItem` 更新 student_id 和 grade。
4. 使用 `GetMistakeItem` 查询更新后的条目。
5. 断言 `student_id` 和 `grade` 为更新后的值，其他字段不变。

## 边界和异常测试

- EX-01 传入全空/全零的 UpdateMistakeItemRequest → 更新成功（无任何字段被改变），返回原条目。
- EX-02 source_regions 包含 BoundingBox=null → 更新成功，BoundingBox 为 null。
- EX-03 多次连续更新同一字段 → 每次更新后值正确变更为最新传入的值。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `UpdateMistakeItemAsync_ShouldUpdateItem` | FR-07 |