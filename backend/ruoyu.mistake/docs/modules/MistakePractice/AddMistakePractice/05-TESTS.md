# AddMistakePractice - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 现有测试用例

### AddPracticeAsync_WhenItemNotConfirmed_ShouldThrow

- **Given** 错题条目存在但 ReviewStatus 为 PendingReview
- **When** 调用 `AddPracticeAsync(practice)`
- **Then** 抛出 `MistakeValidationException`，消息包含"只能对已确认的错题进行练习"

### AddPracticeAsync_WhenItemConfirmed_ShouldAddPractice

- **Given** 错题条目存在且 ReviewStatus 为 Confirmed
- **When** 调用 `AddPracticeAsync(practice)`（practice 包含 MistakeItemId 和 StudentId）
- **Then**
  - 返回结果不为 null
  - `MistakeItemId` 与输入一致
  - `SaveChangesAsync` 被调用一次

## 遗漏的测试场景

| 场景 | 优先级 | 说明 |
|------|--------|------|
| 错题不存在时抛出 MistakeValidationException("错题条目不存在") | 高 | 验证 GetByIdAsync 返回 null |
| 带 imagePaths 参数时正确赋值到 practice.ImagePaths | 高 | 验证可选参数逻辑 |
| imagePaths 为 null 时不修改 practice.ImagePaths 默认值 | 中 | 验证默认行为 |
| imagePaths 为空列表时不修改 practice.ImagePaths | 中 | 验证边界条件 |
| 创建后 ProcessLog 被正确记录（Action=Practiced） | 中 | 验证 AddProcessLogAsync 调用 |
| gRPC 层 MistakeValidationException 转换为 InvalidArgument | 中 | 验证 RpcException 的 StatusCode |
| ReviewStatus 为 Rejected 时也应抛出异常 | 中 | 验证非 Confirmed 状态均拒绝 |
