# AddMistakeItem - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 现有测试用例

### AddMistakeItemAsync_WithSourceRegions_ShouldSucceed

- **Given** 构造一个包含 studentId="student-1"、subject=1、grade=3、sourceUploadId="upload-1" 和两个 SourceRegion（一个含 BoundingBox，一个不含）的 MistakeItemModel
- **When** 调用 `AddMistakeItemAsync(item)`
- **Then**
  - 返回结果的 `Id` 不为空 GUID
  - `SourceRegions` 数量为 2
  - 第一个 SourceRegion 的 BoundingBox 不为 null
  - 第二个 SourceRegion 的 BoundingBox 为 null
  - `ReviewStatus` 为 `PendingReview`
  - `SaveChangesAsync` 被调用一次

## 遗漏的测试场景

| 场景 | 优先级 | 说明 |
|------|--------|------|
| 缺少 studentId 时应抛出 MistakeValidationException | 高 | 验证 "缺少学生ID" |
| subject 越界（<0 或 >9）时应抛出 MistakeValidationException | 高 | 验证 "学科值无效" |
| grade 越界（<0 或 >12）时应抛出 MistakeValidationException | 高 | 验证 "年级值无效" |
| 缺少 sourceUploadId 时应抛出 MistakeValidationException | 高 | 验证 "缺少上传记录ID" |
| sourceRegions 为空列表时应抛出 MistakeValidationException | 高 | 验证 "至少需要一个图片区域" |
| 多个验证同时失败时应拼接错误消息 | 中 | 验证分号分隔的多条错误 |
| 创建后 ProcessLog 被正确记录 | 中 | 验证 AddProcessLogAsync 被调用，Action=Created |
| gRPC 层 MistakeValidationException 转换为 InvalidArgument | 中 | 验证 RpcException 的 StatusCode |
