# DeleteMistakeItem — 测试计划 (TESTS)

测试工具：`xUnit + Moq + FluentAssertions`。现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 删除 Confirmed 状态条目成功（验证 SPEC FR-02/FR-03/FR-04/FR-05/FR-06/FR-07）

- **Given**：存在 ReviewStatus=Confirmed 的条目（Id=某个 Guid）；mock `GetByIdAsync` 返回该条目；mock `DeleteAsync` 返回 true。
- **When**：调用 `MistakeDomainService.DeleteMistakeItemAsync(id)`。
- **Then**：返回 `true`；`RemoveByMistakeItemIdAsync`（练习）被调用一次；`RemoveByMistakeItemIdAsync`（日志）被调用一次；`DeleteAsync` 被调用一次；`SaveChangesAsync` 被调用一次。

### UT-02 删除 PendingReview 状态条目成功（验证 SPEC FR-07）

- **Given**：存在 ReviewStatus=PendingReview 的条目；mock `GetByIdAsync` 返回该条目；mock `DeleteAsync` 返回 true。
- **When**：调用 `DeleteMistakeItemAsync`。
- **Then**：返回 `true`；三级删除均被调用，行为与 Confirmed 状态一致。

### UT-03 条目不存在（验证 SPEC FR-01）

- **Given**：mock `GetByIdAsync` 返回 null。
- **When**：调用 `DeleteMistakeItemAsync(Guid.NewGuid())`。
- **Then**：返回 `false`；`RemoveByMistakeItemIdAsync`（练习）未被调用；`RemoveByMistakeItemIdAsync`（日志）未被调用；`DeleteAsync` 未被调用；`SaveChangesAsync` 未被调用。

### UT-04 删除顺序验证（验证 SPEC FR-02/FR-03/FR-04）

- **Given**：存在条目；三个仓储方法均 mock 为成功。
- **When**：调用 `DeleteMistakeItemAsync`。
- **Then**：调用顺序为：先 `RemoveByMistakeItemIdAsync`（练习记录）→ 再 `RemoveByMistakeItemIdAsync`（操作日志）→ 最后 `DeleteAsync`（错题条目）→ `SaveChangesAsync`。

### UT-05 事务失败 — SaveChangesAsync 抛异常（验证 SPEC FR-08）

- **Given**：存在条目；mock `SaveChangesAsync` 抛出异常。
- **When**：调用 `DeleteMistakeItemAsync`。
- **Then**：异常向上传播；三级删除操作虽被调用但数据未持久化。

### UT-06 gRPC 层返回验证

- **Given**：mock 领域服务 `DeleteMistakeItemAsync` 返回 true。
- **When**：调用 gRPC `DeleteMistakeItem`。
- **Then**：返回 `BoolResponse { Success = true }`，error_message 为空。

### UT-07 gRPC 层条目不存在返回

- **Given**：mock 领域服务 `DeleteMistakeItemAsync` 返回 false。
- **When**：调用 gRPC `DeleteMistakeItem`。
- **Then**：返回 `BoolResponse { Success = false }`。

## 集成测试

### IT-01 端到端删除 → 确认不可查

1. 使用真实的测试数据库上下文。
2. 先调用 `AddMistakeItem` 创建一条错题条目。
3. 调用 `DeleteMistakeItem` 删除该条目。
4. 调用 `GetMistakeItem` 查询该条目。
5. 断言返回 NotFound（条目已被物理删除）。

## 边界和异常测试

- EX-01 删除已有练习记录的条目 → 练习记录和错题条目均被删除。
- EX-02 删除已有操作日志的条目 → 操作日志和错题条目均被删除。
- EX-03 删除无练习记录也无操作日志的条目 → 删除成功（RemoveByXxx 执行但无实际记录可删除）。
- EX-04 重复删除同一条目 → 第二次返回 false（条目已不存在）。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `DeleteMistakeItemAsync_WhenConfirmed_ShouldDelete` | FR-02, FR-03, FR-04, FR-05, FR-06, FR-07 |
| `DeleteMistakeItemAsync_WhenPendingReview_ShouldDelete` | FR-07 |