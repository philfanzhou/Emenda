# 错题列表查询 — 设计说明 (DESIGN)

## 文件结构

```
src/
├── Contract/Protos/mistake.proto              # gRPC 定义: GetMistakeItemList RPC
├── Contract/Protos/mistake.common.proto       # 消息定义: PageMeta, ReviewStatus
├── Service/MistakeServiceImpl.cs              # gRPC 实现: GetMistakeItemList handler
├── Domain/Services/MistakeDomainService.cs    # 领域服务: GetMistakeItemListAsync
├── Domain/Repositories/IMistakeItemRepository.cs  # 仓储接口: GetListAsync
└── Database/Repositories/MistakeItemRepository.cs  # 仓储实现: GetListAsync
```

## 关键接口签名

### gRPC (proto)

```protobuf
rpc GetMistakeItemList(GetMistakeItemListRequest) returns (MistakeItemPageResult);

message GetMistakeItemListRequest {
    string student_id = 1;
    int32 subject = 2;
    int32 grade = 3;
    ReviewStatus review_status = 4;
    int32 page = 5;
    int32 size = 6;
}

message MistakeItemPageResult {
    repeated MistakeItemDto items = 1;
    PageMeta page_meta = 2;
}
```

### Domain Service

```csharp
public async Task<(List<MistakeItemModel> Items, int TotalCount)> GetMistakeItemListAsync(
    string studentId, int subject, int grade, int reviewStatus, int page, int size)
```

### Repository

```csharp
public async Task<(List<MistakeItemModel> Items, int TotalCount)> GetListAsync(
    string studentId, int subject, int grade, int reviewStatus, int page, int size)
```

## 数据流

```
Client → gRPC:
  GetMistakeItemListRequest { studentId, subject?, grade?, reviewStatus?, page, size }

MistakeServiceImpl.GetMistakeItemList:
  1. 调用 _mistakeService.GetMistakeItemListAsync(...)
  2. 构建 PageMeta (page, size, totalCount, totalPages)
  3. Map 每个 MistakeItemModel → MistakeItemDto
  4. 返回 MistakeItemPageResult

MistakeDomainService.GetMistakeItemListAsync:
  1. 参数修正: page<=0→1, size<=0→10, size>100→100
  2. 调用 _mistakeItemRepository.GetListAsync(...)

MistakeItemRepository.GetListAsync:
  1. WHERE student_id = @studentId
  2. AND subject = @subject (if >0)
  3. AND grade = @grade (if >0)
  4. AND review_status = @reviewStatus (if >0)
  5. COUNT total
  6. ORDER BY created_at DESC
  7. SKIP (page-1)*size, TAKE size
```

## 依赖的数据库

| 表 | 字段 | 用途 |
|----|------|------|
| [mistake_item](../../../database/tables/mistake_item.md) | student_id, subject, grade, review_status | 筛选条件 |
| [mistake_item](../../../database/tables/mistake_item.md) | created_at | 排序 |
| [mistake_item](../../../database/tables/mistake_item.md) | 全部字段 | 返回数据 |

## 关键设计决策

- **分页参数修正**：在 Domain Service 层统一处理，保证 Repository 不需要关心边界值
- **复合索引**：`(student_id, subject, grade, review_status)` 覆盖常见查询组合
- **排序**：使用 `OrderByDescending` 进行排序
- **totalPages 计算**：在 gRPC 层使用整数除法 `(totalCount + size - 1) / size`，向上取整
