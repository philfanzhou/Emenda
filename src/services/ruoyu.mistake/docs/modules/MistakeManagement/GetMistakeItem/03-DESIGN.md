# 错题条目查询 — 设计说明 (DESIGN)

## 文件结构

```
src/
├── Contract/Protos/mistake.proto              # gRPC 定义: GetMistakeItem RPC
├── Contract/Protos/mistake.common.proto       # 消息定义: IdRequest, MistakeItemDto
├── Service/MistakeServiceImpl.cs              # gRPC 实现: GetMistakeItem handler
├── Domain/Services/MistakeDomainService.cs    # 领域服务: GetMistakeItemAsync
├── Domain/Repositories/IMistakeItemRepository.cs  # 仓储接口: GetByIdAsync
└── Database/Repositories/MistakeItemRepository.cs  # 仓储实现: GetByIdAsync (FindAsync)
```

## 关键接口签名

### gRPC (proto)

```protobuf
rpc GetMistakeItem(IdRequest) returns (MistakeItemDto);

message IdRequest {
    string id = 1;
}
```

### Domain Service

```csharp
public async Task<MistakeItemModel?> GetMistakeItemAsync(Guid id)
```

### Repository

```csharp
Task<MistakeItemModel?> GetByIdAsync(Guid id)
```

## 数据流

```
Client → gRPC:
  IdRequest { id }

MistakeServiceImpl.GetMistakeItem:
  1. Guid.Parse(request.Id)
  2. 调用 _mistakeService.GetMistakeItemAsync(guid)
  3. 若 item == null → throw RpcException(NotFound, "错题条目不存在")
  4. MapMistakeItemDto(item) → MistakeItemDto
  5. 返回 MistakeItemDto

MistakeDomainService.GetMistakeItemAsync:
  1. 调用 _mistakeItemRepository.GetByIdAsync(id)
  2. 返回 MistakeItemModel? (可能为 null)

MistakeItemRepository.GetByIdAsync:
  1. _dbContext.MistakeItems.FindAsync(id)
  2. 若 entity != null → MapToModel(entity)
  3. 否则 → null
```

## 依赖的数据库

| 表 | 字段 | 用途 |
|----|------|------|
| [mistake_item](../../database/tables/mistake_item.md) | id (PK) | 主键查询 |
| [mistake_item](../../database/tables/mistake_item.md) | 全部字段 | 返回数据 |

## 关键设计决策

- **简单透传**: Domain Service 和 Repository 均为简单透传，无额外业务逻辑
- **null → NotFound**: gRPC 层将 null 结果映射为 `StatusCode.NotFound`，符合 gRPC 惯例
- **Guid.Parse**: 在 gRPC 层解析字符串为 Guid，Domain Service 使用强类型参数
