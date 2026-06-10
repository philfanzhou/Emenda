# 按上传记录查询错题 — 测试计划 (TESTS)

测试工具：xUnit + Moq + FluentAssertions。
现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 已有测试

当前无专门测试覆盖 `GetMistakeItemsByUploadAsync`。

## 遗漏的测试场景

### UT-01: 有错题时返回列表

- **Given**: Mock `GetBySourceUploadIdAsync("upload-1")` 返回 3 条错题
- **When**: 调用 `GetMistakeItemsByUploadAsync("upload-1")`
- **Then**: 返回 3 条 `MistakeItemModel`

### UT-02: 无错题时返回空列表

- **Given**: Mock `GetBySourceUploadIdAsync("upload-empty")` 返回空列表
- **When**: 调用 `GetMistakeItemsByUploadAsync("upload-empty")`
- **Then**: 返回空列表

### UT-03: gRPC 层正确映射为 MistakeItemListResponse

- **Given**: Domain Service 返回 2 条 `MistakeItemModel`
- **When**: gRPC handler 执行
- **Then**: 返回 `MistakeItemListResponse`，`items` 包含 2 条 `MistakeItemDto`

### UT-04: 客户端排序验证（Repository 层集成测试）

- **Given**: 数据库中有 3 条错题，CreatedAt 分别为 T1 < T2 < T3
- **When**: 调用 `GetBySourceUploadIdAsync`
- **Then**: 返回结果按 CreatedAt 升序排列
