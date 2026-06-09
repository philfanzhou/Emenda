# GetPendingReviewUploads — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：待创建。

## 单元测试 — Given-When-Then 格式

### UT-01 分页查询返回正确结果（验证 SPEC FR-01/FR-02/FR-05）

- **Given**：Mock 仓储返回 2 条 `PendingReviewUploadModel` 和 `totalCount = 2`。
- **When**：调用 `MistakeDomainService.GetPendingReviewUploadsAsync(1, 10, null)`。
- **Then**：返回元组 `Uploads.Count == 2`、`TotalCount == 2`；每条记录的 `SourceUploadId` 非空、`PendingCount >= 1`、`TotalCount >= PendingCount`。

### UT-02 分页参数修正（验证 SPEC FR-07）

- **Given**：Mock 仓储任意返回。
- **When**：调用 `GetPendingReviewUploadsAsync(0, 0, null)`。
- **Then**：仓储收到的 `page` 参数为 1，`size` 参数为 10。
- **When**：调用 `GetPendingReviewUploadsAsync(1, 200, null)`。
- **Then**：仓储收到的 `size` 参数为 100。

### UT-03 学科筛选传递（验证 SPEC FR-06）

- **Given**：Mock 仓储。
- **When**：调用 `GetPendingReviewUploadsAsync(1, 10, [1, 2])`。
- **Then**：仓储 `GetPendingReviewUploadsAsync` 被调用一次，且 `subjects` 参数为 `[1, 2]`。
- **When**：调用 `GetPendingReviewUploadsAsync(1, 10, null)`。
- **Then**：仓储收到的 `subjects` 参数为 null。

### UT-04 gRPC 层 subjects 过滤处理

- **Given**：`GetPendingReviewUploadsRequest { Subjects = [3, 4] }`。
- **When**：调用 gRPC `GetPendingReviewUploads` 方法。
- **Then**：领域服务收到的 `subjects` 为 `[3, 4]`。
- **Given**：`GetPendingReviewUploadsRequest { Subjects = [] }`。
- **When**：调用 gRPC `GetPendingReviewUploads` 方法。
- **Then**：领域服务收到的 `subjects` 为 null。

### UT-05 空结果正常返回（验证 SPEC FR-08）

- **Given**：Mock 仓储返回空列表和 `totalCount = 0`。
- **When**：调用 `GetPendingReviewUploadsAsync(1, 10, null)`。
- **Then**：返回元组 `Uploads.Count == 0`、`TotalCount == 0`；不抛出异常。

### UT-06 PageMeta 计算正确（验证 SPEC FR-05）

- **Given**：Mock 仓储返回 `totalCount = 25`。
- **When**：调用 `GetPendingReviewUploadsAsync(1, 10, null)`，gRPC 层构建 `PendingReviewUploadListResponse`。
- **Then**：响应 `page_meta.page == 1`、`page_meta.size == 10`、`page_meta.total_count == 25`、`page_meta.total_pages == 3`。

### UT-07 gRPC 层 EarliestCreatedAt 为 MinValue 时返回空字符串

- **Given**：Mock 领域服务返回 `PendingReviewUploadModel` 其中 `EarliestCreatedAt = DateTimeOffset.MinValue`。
- **When**：gRPC 层映射为 `PendingReviewUploadDto`。
- **Then**：`earliest_created_at` 为空字符串。

## 集成测试

### IT-01 端到端分页查询

1. 使用真实的测试数据库上下文。
2. 插入多条错题记录，部分为 PendingReview、部分为 Confirmed/Rejected，属于不同的 `source_upload_id`。
3. 调用 `GetPendingReviewUploads` gRPC 方法。
4. 断言仅返回包含 PendingReview 记录的上传分组。
5. 断言每条记录的 `pending_count` 和 `total_count` 正确。
6. 断言 `page_meta.total_pages` 正确。

## 边界和异常测试

- EX-01 page=0 → 自动修正为 page=1。
- EX-02 page=-1 → 自动修正为 page=1。
- EX-03 size=0 → 自动修正为 size=10。
- EX-04 size=101 → 自动修正为 size=100。
- EX-05 size=100 → 正常传递（边界值）。
- EX-06 subjects 含无效学科 ID → 仓储查询无匹配结果，返回空列表。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `GetPendingReviewUploadsAsync_ReturnsPaginated` | FR-01, FR-02, FR-05 |
| `GetPendingReviewUploadsAsync_PageParamCorrection` | FR-07 |
| `GetPendingReviewUploadsAsync_SubjectFilter` | FR-06 |
| `GetPendingReviewUploadsAsync_EmptyResult` | FR-08 |
| `GetPendingReviewUploads_PageMetaCorrect` | FR-05 |
| `GetPendingReviewUploads_MinValueDateTime` | FR-02 |