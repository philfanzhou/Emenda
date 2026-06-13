# 错题处理历史查询 - 设计文档

## 文件结构

| 文件 | 项目相对路径 | 说明 |
|------|-------------|------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` | RPC: `GetMistakeProcessHistory(IdRequest) returns (ProcessLogListResponse)` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` | `ProcessLogDto`, `ProcessAction`, `IdRequest` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` | `GetMistakeProcessHistory` 方法 |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` | `GetMistakeProcessHistoryAsync(Guid)` |
| 仓储接口 | `src/Domain/Repositories/IProcessLogRepository.cs` | `GetByMistakeItemIdAsync(Guid)` |
| 仓储实现 | `src/Database/Repositories/ProcessLogRepository.cs` | EF Core 查询 + OrderBy |
| 领域模型 | `src/Domain/Models/ProcessLogModel.cs` | ProcessLog 领域模型 |
| 数据库实体 | `src/Database/Entities/ProcessLogEntity.cs` | `process_log` 表实体 |

## 关键接口签名

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<ProcessLogListResponse> GetMistakeProcessHistory(IdRequest request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<List<ProcessLogModel>> GetMistakeProcessHistoryAsync(Guid mistakeItemId)
```

### 仓储接口

```csharp
// IProcessLogRepository.cs
Task<List<ProcessLogModel>> GetByMistakeItemIdAsync(Guid mistakeItemId)
```

### 仓储实现

```csharp
// ProcessLogRepository.cs
public async Task<List<ProcessLogModel>> GetByMistakeItemIdAsync(Guid mistakeItemId)
// SQL: SELECT * FROM process_log WHERE mistake_item_id = @id ORDER BY created_at
```

## 依赖的数据库表

| 表名 | 关键字段 | 说明 |
|------|---------|------|
| `process_log` | `id`, `mistake_item_id`, `action`, `details`, `created_at` | 处理日志表，详见 `database/` 目录 |

## 数据流/调用链

```
Client
  │
  ▼
MistakeServiceImpl.GetMistakeProcessHistory(IdRequest)
  │  Guid.Parse(request.Id)
  ▼
MistakeDomainService.GetMistakeProcessHistoryAsync(Guid mistakeItemId)
  │
  ▼
IProcessLogRepository.GetByMistakeItemIdAsync(Guid mistakeItemId)
  │
  ▼
ProcessLogRepository (EF Core)
  │  SELECT * FROM process_log
  │  WHERE mistake_item_id = @id
  │  ORDER BY created_at
  ▼
List<ProcessLogModel>
  │
  ▼
MapProcessLogDto() → ProcessLogDto (Action 枚举映射)
  │
  ▼
ProcessLogListResponse { logs: [...] }
```

## 关键设计决策

1. **无分页**: 当前设计返回全部日志，因为单个错题的处理日志数量有限（通常 < 20 条），不需要分页。
2. **不校验错题存在性**: 领域服务直接查询 ProcessLog，不先查询 MistakeItem 是否存在。这是有意为之——即使错题被删除，历史日志仍可查询。
3. **排序在仓储层完成**: `OrderBy(p => p.CreatedAt)` 在 SQL 层面完成排序，确保时间线顺序。
4. **Guid.Parse 无保护**: gRPC 层直接使用 `Guid.Parse(request.Id)`，无效格式会抛出 `FormatException`，由 gRPC 框架转为 Internal 错误。
