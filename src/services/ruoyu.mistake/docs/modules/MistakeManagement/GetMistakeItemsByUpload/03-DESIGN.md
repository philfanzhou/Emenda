# 按上传记录查询错题 — 设计说明 (DESIGN)

## 文件结构

```
src/
├── Contract/Protos/mistake.proto              # gRPC 定义: GetMistakeItemsByUpload RPC
├── Contract/Protos/mistake.common.proto       # 消息定义: MistakeItemDto
├── Service/MistakeServiceImpl.cs              # gRPC 实现: GetMistakeItemsByUpload handler
├── Domain/Services/MistakeDomainService.cs    # 领域服务: GetMistakeItemsByUploadAsync
├── Domain/Repositories/IMistakeItemRepository.cs  # 仓储接口: GetBySourceUploadIdAsync
└── Database/Repositories/MistakeItemRepository.cs  # 仓储实现: GetBySourceUploadIdAsync
```

## 关键接口签名

### gRPC (proto)

```protobuf
rpc GetMistakeItemsByUpload(GetMistakeItemsByUploadRequest) returns (MistakeItemListResponse);

message GetMistakeItemsByUploadRequest {
    string source_upload_id = 1;
}

message MistakeItemListResponse {
    repeated MistakeItemDto items = 1;
}
```

### Domain Service

```csharp
public async Task<List<MistakeItemModel>> GetMistakeItemsByUploadAsync(string sourceUploadId)
```

### Repository

```csharp
Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId)
```

## 数据流

```
Client → gRPC:
  GetMistakeItemsByUploadRequest { sourceUploadId }

MistakeServiceImpl.GetMistakeItemsByUpload:
  1. 调用 _mistakeService.GetMistakeItemsByUploadAsync(sourceUploadId)
  2. 遍历 items → MapMistakeItemDto
  3. 返回 MistakeItemListResponse { items }

MistakeDomainService.GetMistakeItemsByUploadAsync:
  1. 调用 _mistakeItemRepository.GetBySourceUploadIdAsync(sourceUploadId)
  2. 返回 List<MistakeItemModel>

MistakeItemRepository.GetBySourceUploadIdAsync:
  1. WHERE source_upload_id = @sourceUploadId
  2. ToListAsync() → 获取全部数据
  3. 客户端排序: OrderBy(m => m.CreatedAt)
  4. Select(MapToModel) → List<MistakeItemModel>
```

## 依赖的数据库

| 表 | 字段 | 用途 |
|----|------|------|
| [mistake_item](../../database/tables/mistake_item.md) | source_upload_id | 筛选条件 |
| [mistake_item](../../database/tables/mistake_item.md) | created_at | 客户端排序 |
| [mistake_item](../../database/tables/mistake_item.md) | 全部字段 | 返回数据 |

## 关键设计决策

- **客户端排序**: 在客户端使用 LINQ `OrderBy` 排序
- **无分页**: 该接口用于查看单次上传的全部错题，数据量有限（通常不超过 20 条），不需要分页
- **简单透传**: Domain Service 直接透传 Repository 结果，无额外业务逻辑
