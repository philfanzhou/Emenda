# 错题列表查询 — 测试计划 (TESTS)

测试工具：xUnit + Moq + FluentAssertions。
现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 已有测试

当前无专门测试覆盖 `GetMistakeItemListAsync` 的分页修正逻辑。`page<=0→1`, `size<=0→10`, `size>100→100` 在 Domain Service 中实现但未单独测试。

## 遗漏的测试场景

### UT-01: page=0 时修正为 1

- **Given**: Mock Repository 返回任意数据
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: 0, size: 10)`
- **Then**: Repository.GetListAsync 被调用时 page=1

### UT-02: size=200 时修正为 100

- **Given**: Mock Repository 返回任意数据
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: 1, size: 200)`
- **Then**: Repository.GetListAsync 被调用时 size=100

### UT-03: size=0 时修正为 10

- **Given**: Mock Repository 返回任意数据
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: 1, size: 0)`
- **Then**: Repository.GetListAsync 被调用时 size=10

### UT-04: 正常分页返回

- **Given**: Mock Repository 返回 15 条数据，totalCount=25
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: 1, size: 10)`
- **Then**: 返回 10 条 items，totalCount=25

### UT-05: page 为负数时修正为 1

- **Given**: Mock Repository 返回任意数据
- **When**: 调用 `GetMistakeItemListAsync(studentId, 0, 0, 0, page: -1, size: 10)`
- **Then**: Repository.GetListAsync 被调用时 page=1

### UT-06: gRPC 层 PageMeta 构建正确

- **Given**: Domain Service 返回 totalCount=25
- **When**: gRPC handler 构建 PageMeta（page=1, size=10）
- **Then**: PageMeta.totalPages=3, PageMeta.totalCount=25
