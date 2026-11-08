# UpdateMistakeItem - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 现有测试用例

### UpdateMistakeItemAsync_ShouldUpdateItem

- **Given** 构造一个 Id 和 StudentId 已设置的 MistakeItemModel，仓储 UpdateAsync 返回 true
- **When** 调用 `UpdateMistakeItemAsync(item)`
- **Then**
  - 返回 true
  - `SaveChangesAsync` 被调用一次

## 遗漏的测试场景

| 场景 | 优先级 | 说明 |
|------|--------|------|
| 错题 ID 不存在时 gRPC 层返回 NotFound | 高 | 验证 GetMistakeItemAsync 返回 null 时的处理 |
| 仅提供 studentId 时只更新 studentId | 高 | 验证部分更新语义 |
| 仅提供 subject > 0 时只更新 subject | 高 | 验证部分更新语义 |
| 仅提供 grade > 0 时只更新 grade | 高 | 验证部分更新语义 |
| 提供 sourceRegions 时整体替换 | 高 | 验证 SourceRegion 映射和替换逻辑 |
| 所有字段均为空/零时不修改任何字段 | 中 | 验证空请求的安全性 |
| 领域服务 UpdateMistakeItemAsync 返回 false 时 gRPC 层抛出 Internal | 中 | 验证错误转换 |
| MistakeValidationException 转换为 InvalidArgument | 中 | 验证异常映射 |
| 更新后重新查询返回最新数据 | 中 | 验证返回值一致性 |
| 重新查询返回 null 时抛出 NotFound | 低 | 验证极端情况 |
