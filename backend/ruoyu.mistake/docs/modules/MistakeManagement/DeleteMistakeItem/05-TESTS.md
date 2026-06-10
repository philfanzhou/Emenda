# DeleteMistakeItem - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 现有测试用例

### DeleteMistakeItemAsync_WhenConfirmed_ShouldDelete

- **Given** 存在一个 ReviewStatus=Confirmed 的错题条目，仓储 DeleteAsync 返回 true
- **When** 调用 `DeleteMistakeItemAsync(itemId)`
- **Then**
  - 返回 true
  - `RemoveByMistakeItemIdAsync` 在 PracticeRepo 上被调用一次
  - `RemoveByMistakeItemIdAsync` 在 LogRepo 上被调用一次
  - `DeleteAsync` 在 ItemRepo 上被调用一次
  - `SaveChangesAsync` 被调用一次

### DeleteMistakeItemAsync_WhenPendingReview_ShouldDelete

- **Given** 存在一个 ReviewStatus=PendingReview 的错题条目，仓储 DeleteAsync 返回 true
- **When** 调用 `DeleteMistakeItemAsync(itemId)`
- **Then**
  - 返回 true
  - `RemoveByMistakeItemIdAsync` 在 PracticeRepo 上被调用一次
  - `RemoveByMistakeItemIdAsync` 在 LogRepo 上被调用一次
  - `DeleteAsync` 在 ItemRepo 上被调用一次
  - `SaveChangesAsync` 被调用一次

## 遗漏的测试场景

| 场景 | 优先级 | 说明 |
|------|--------|------|
| 错题不存在时返回 false | 高 | 验证 GetByIdAsync 返回 null 的场景 |
| 验证删除顺序：practices 先于 logs，logs 先于 item | 中 | 使用 Moq 的 CallBase 或 Callback 验证调用顺序 |
| 子表无数据时仍正常删除主表 | 中 | 验证 RemoveByMistakeItemIdAsync 无数据时的行为 |
| MistakeValidationException 转换为 InvalidArgument | 中 | 验证 gRPC 层异常映射 |
| SaveChangesAsync 失败时的行为 | 低 | 验证事务回滚 |
