# 错题条目查询 — 测试计划 (TESTS)

测试工具：xUnit + Moq + FluentAssertions。
现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 已有测试

当前无专门测试覆盖 `GetMistakeItemAsync`。

## 遗漏的测试场景

### UT-01: 有效 ID 返回错题条目

- **Given**: Mock `GetByIdAsync(validId)` 返回 `MistakeItemModel { Id=validId, StudentId="s1" }`
- **When**: 调用 `GetMistakeItemAsync(validId)`
- **Then**: 返回的 model 不为 null，`Id` 等于 `validId`

### UT-02: 不存在的 ID 返回 null

- **Given**: Mock `GetByIdAsync(unknownId)` 返回 `null`
- **When**: 调用 `GetMistakeItemAsync(unknownId)`
- **Then**: 返回 `null`

### UT-03: gRPC 层 null 映射为 NotFound

- **Given**: Domain Service 返回 `null`
- **When**: gRPC handler 执行
- **Then**: 抛出 `RpcException(StatusCode.NotFound, "错题条目不存在")`

### UT-04: gRPC 层有效结果映射为 MistakeItemDto

- **Given**: Domain Service 返回有效的 `MistakeItemModel`
- **When**: gRPC handler 执行
- **Then**: 返回 `MistakeItemDto`，字段正确映射（含 SourceRegions、ReviewStatus 等）
