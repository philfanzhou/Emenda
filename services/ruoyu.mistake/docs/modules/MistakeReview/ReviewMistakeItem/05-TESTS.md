# ReviewMistakeItem - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 单元测试

### ReviewMistakeItemAsync_WhenPending_ShouldConfirm

- **Given** 错题条目 ReviewStatus 为 PendingReview
- **When** 调用 ReviewMistakeItemAsync(id, Confirmed, "teacher-1", "looks good")
- **Then** 返回非 null 结果，ReviewStatus 更新为 Confirmed，ReviewerId 为 "teacher-1"，ReviewComment 为 "looks good"，ReviewedAt 不为 null，SaveChangesAsync 被调用一次

### ReviewMistakeItemAsync_WhenAlreadyReviewed_ShouldThrow

- **Given** 错题条目 ReviewStatus 为 Confirmed
- **When** 调用 ReviewMistakeItemAsync(id, Rejected, "teacher-1", null)
- **Then** 抛出 MistakeValidationException，消息包含"只能审核待审核状态"

### ReviewMistakeItemAsync_WhenPending_ShouldReject

- **Given** 错题条目 ReviewStatus 为 PendingReview
- **When** 调用 ReviewMistakeItemAsync(id, Rejected, "teacher-1", "需要重新上传")
- **Then** 返回非 null 结果，ReviewStatus 更新为 Rejected，ReviewComment 为 "需要重新上传"，SaveChangesAsync 被调用一次

## 遗漏的测试场景

| 场景 | 说明 | 优先级 |
|------|------|--------|
| 审核结果为非 Confirmed/Rejected 时应抛出 MistakeValidationException | 验证审核结果枚举 | 高 |
| 错题不存在时返回 null | 验证 GetByIdAsync 返回 null 的处理 | 高 |
| 可选 grade 更新（有效值 1-12） | 验证 grade 更新逻辑 | 中 |
| 可选 grade 更新（无效值被忽略） | 验证 grade<=0 或 >12 不更新 | 中 |
| 可选 subject 更新（有效值 1-9） | 验证 subject 更新逻辑 | 中 |
| 可选 subject 更新（无效值被忽略） | 验证 subject<=0 或 >9 不更新 | 中 |
| ProcessLog 记录验证（Confirmed） | 验证 Action=ReviewConfirmed, details="Mistake item confirmed" | 中 |
| ProcessLog 记录验证（Rejected） | 验证 Action=ReviewRejected, details="Mistake item rejected" | 中 |
| gRPC 层图片迁移逻辑 | Confirmed 时 uploads/→mistakes/ 的 Copy+Delete | 中 |
| gRPC 层非 uploads/ 前缀图片不迁移 | 验证路径前缀判断 | 低 |
| gRPC 屙 removed_image_paths 收集 | 验证所有 SourceImagePath 被收集 | 低 |
| gRPC 层审核状态验证 | 非 Confirmed/Rejected 直接返回 InvalidArgument | 低 |
