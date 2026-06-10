# GetMistakePractices - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 现有测试用例

无。当前 `GetPracticesAsync` 没有对应的单元测试。

## 遗漏的测试场景

| 场景 | 优先级 | 说明 |
|------|--------|------|
| GetPracticesAsync 返回指定错题的练习记录 | 高 | 验证基本查询功能 |
| 无练习记录时返回空列表 | 高 | 验证空结果处理 |
| 结果按 PracticeDate 升序排列 | 高 | 验证排序逻辑 |
| 错题 ID 不存在时返回空列表 | 中 | 验证不抛异常 |
| 练习记录包含 imagePaths 字段 | 中 | 验证 JSONB 字段映射 |
| gRPC 层 MistakePracticeListResponse 格式正确 | 中 | 验证响应映射 |
