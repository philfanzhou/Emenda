# 错题列表查询 — 测试计划 (TESTS)

测试工具：xUnit + Moq + FluentAssertions。  
现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 已有测试

### UT-01: 分页参数修正

当前无专门测试覆盖分页修正逻辑。`page<=0→1`, `size<=0→10`, `size>100→100` 在 Domain Service 中实现但未单独测试。

## 遗漏的测试场景

### UT-02: page=0 时修正为 1

- **Given**: Mock Repository 返回任意数据
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: 0, size: 10)`
- **Then**: Repository.GetListAsync 被调用时 page=1

### UT-03: size=200 时修正为 100

- **Given**: Mock Repository 返回任意数据
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: 1, size: 200)`
- **Then**: Repository.GetListAsync 被调用时 size=100

### UT-04: size=0 时修正为 10

- **Given**: Mock Repository 返回任意数据
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: 1, size: 0)`
- **Then**: Repository.GetListAsync 被调用时 size=10

### UT-05: 正常分页返回

- **Given**: Mock Repository 返回 15 条数据，totalCount=25
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: 1, size: 10)`
- **Then**: 返回 10 条 items，totalCount=25