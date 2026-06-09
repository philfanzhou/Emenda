# GetMistakeItem — 测试计划 (TESTS)

测试工具：`xUnit + Moq + FluentAssertions`。现有测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### GetMistakeItem 单条查询

#### UT-01 查询存在条目成功（验证 SPEC FR-01）

- **Given**：mock `GetByIdAsync` 返回有效的 `MistakeItemModel`（含 SourceRegions 和 BoundingBox）。
- **When**：调用 `MistakeDomainService.GetMistakeItemAsync(id)`。
- **Then**：返回非 null 的 `MistakeItemModel`，字段值与 mock 数据一致。

#### UT-02 查询不存在条目（验证 SPEC FR-01）

- **Given**：mock `GetByIdAsync` 返回 null。
- **When**：调用 `MistakeDomainService.GetMistakeItemAsync(Guid.NewGuid())`。
- **Then**：返回 null；gRPC 层应转换为 `RpcException(StatusCode.NotFound)`。

#### UT-03 DTO 映射 — SourceRegions 含 BoundingBox（验证 SPEC FR-09）

- **Given**：`MistakeItemModel` 含一个 SourceRegion（BoundingBox 非 null）。
- **When**：调用 `MapMistakeItemDto`。
- **Then**：返回的 `MistakeItemDto.SourceRegions[0].BoundingBox.X1/Y1/X2/Y2` 与输入一致。

#### UT-04 DTO 映射 — SourceRegions 不含 BoundingBox（验证 SPEC FR-09）

- **Given**：`MistakeItemModel` 含一个 SourceRegion（BoundingBox=null）。
- **When**：调用 `MapMistakeItemDto`。
- **Then**：返回的 `MistakeItemDto.SourceRegions[0].BoundingBox` 为 null。

#### UT-05 DTO 映射 — 时间字段为 MinValue（验证 SPEC FR-09）

- **Given**：`MistakeItemModel.CreatedAt = DateTimeOffset.MinValue`。
- **When**：调用 `MapMistakeItemDto`。
- **Then**：返回的 `MistakeItemDto.CreatedAt` 为空字符串 `""`。

### GetMistakeItemsByUpload 按上传记录查询

#### UT-06 查询成功返回列表（验证 SPEC FR-04）

- **Given**：mock `GetBySourceUploadIdAsync` 返回含 3 条记录的列表。
- **When**：调用 `MistakeDomainService.GetMistakeItemsByUploadAsync("upload-1")`。
- **Then**：返回 `List.Count == 3`，每条记录映射为正确的 `MistakeItemDto`。

#### UT-07 查询无结果返回空列表

- **Given**：mock `GetBySourceUploadIdAsync` 返回空列表。
- **When**：调用 `GetMistakeItemsByUploadAsync("nonexistent")`。
- **Then**：返回 `List.Count == 0`，gRPC 层 `MistakeItemListResponse.Items` 为空。

### GetMistakeItemList 分页条件查询

#### UT-08 分页默认值规范化（验证 SPEC FR-05）

- **Given**：调用 `GetMistakeItemListAsync` 时传入 page=0, size=0。
- **When**：领域服务执行。
- **Then**：实际上 page 被修正为 1，size 被修正为 10；`GetListAsync` 接收到 page=1, size=10。

#### UT-09 size 超过最大值截断（验证 SPEC FR-06）

- **Given**：调用时传入 size=200。
- **When**：领域服务执行。
- **Then**：`GetListAsync` 接收到 size=100。

#### UT-10 过滤条件传递正确（验证 SPEC FR-07）

- **Given**：传入 student_id="student-1", subject=3, grade=5, reviewStatus=1。
- **When**：调用 `GetMistakeItemListAsync`。
- **Then**：`GetListAsync` 接收到的参数值与传入一致。

#### UT-11 PageMeta 计算正确（验证 SPEC FR-08）

- **Given**：mock `GetListAsync` 返回 items 5 条、totalCount=23，传入 page=2, size=10。
- **When**：调用 `GetMistakeItemListAsync`。
- **Then**：返回的 page_meta 中 `page=2`, `size=10`, `total_count=23`, `total_pages=3`（(23+10-1)/10=3）。

#### UT-12 空结果分页

- **Given**：mock `GetListAsync` 返回 items 0 条、totalCount=0。
- **When**：调用 `GetMistakeItemListAsync`。
- **Then**：返回 items 为空，page_meta 中 `total_count=0`, `total_pages=0`（(0+10-1)/10=0）。

## 集成测试

### IT-01 端到端创建 → 查询

1. 使用真实的测试数据库上下文。
2. 调用 `AddMistakeItem` 创建多条错题条目。
3. 调用 `GetMistakeItem` 按 ID 查询单条，验证字段正确。
4. 调用 `GetMistakeItemsByUpload` 按 source_upload_id 查询，验证返回数量正确。
5. 调用 `GetMistakeItemList` 分页查询，验证 page_meta 和 items 正确。

## 边界和异常测试

- EX-01 GetMistakeItem incoming 非 GUID 格式的 ID → `Guid.Parse` 抛出异常，由 gRPC 框架处理。
- EX-02 GetMistakeItemList page 为负数 → 修正为 1。
- EX-03 GetMistakeItemList size 为负数 → 修正为 10。
- EX-04 查询结果的 SourceRegions 为空列表 → DTO 映射正常，SourceRegions 为空。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| （待补充）`GetMistakeItemAsync_ShouldReturnItem` | FR-01, FR-02 |
| （待补充）`GetMistakeItemAsync_WhenNotFound_ShouldReturnNull` | FR-01 |
| （待补充）`GetMistakeItemsByUpload_ShouldReturnList` | FR-03, FR-04 |
| （待补充）`GetMistakeItemList_ShouldNormalizePageSize` | FR-05, FR-06 |
| （待补充）`GetMistakeItemList_ShouldFilter` | FR-07 |
| （待补充）`GetMistakeItemList_ShouldComputePageMeta` | FR-08 |