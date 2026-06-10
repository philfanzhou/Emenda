# GetPendingReviewUploads - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`

## 单元测试

### GetPendingReviewUploadsAsync_ShouldReturnPendingItems

- **Given** 仓储层 GetPendingReviewUploadsAsync(1, 10, null) 返回 1 个 PendingReviewUploadModel 和 totalCount=1
- **When** 调用 GetPendingReviewUploadsAsync(1, 10)
- **Then** 返回 Uploads 包含 1 个元素，TotalCount 为 1

## 遗漏的测试场景

| 场景 | 说明 | 优先级 |
|------|------|--------|
| page<=0 时修正为 1 | 验证分页参数修正 | 高 |
| size<=0 时修正为 10 | 验证分页参数修正 | 高 |
| size>100 时修正为 100 | 验证分页参数修正 | 高 |
| subjects 筛选传递 | 验证 subjects 参数正确传递到仓储层 | 中 |
| subjects 为空列表时传 null | 验证空列表不作为筛选条件 | 中 |
| 空结果返回 | 无 PendingReview 条目时返回空列表和 totalCount=0 | 中 |
| 分页元数据计算 | 验证 PageMeta 的 total_pages 计算（向上取整） | 低 |
| EarliestCreatedAt 格式化 | MinValue→空字符串，有效值→ISO 8601 UTC | 低 |
| 仓储层客户端分组聚合 | 验证分组键和聚合逻辑 | 低 |
| 仓储层排序 | 验证按 EarliestCreatedAt 升序 | 低 |
