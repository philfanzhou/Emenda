# GetMistakeProcessHistory — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── ProcessLogModel.cs               # 处理日志领域模型
│   │   ├── Services/
│   │   │   └── MistakeDomainService.cs          # 领域服务 (GetMistakeProcessHistoryAsync)
│   │   └── Repositories/
│   │       └── IProcessLogRepository.cs         # 处理日志仓储接口
│   ├── Service/
│   │   └── MistakeServiceImpl.cs               # gRPC 实现 (GetMistakeProcessHistory)
│   ├── Database/
│   │   ├── Entities/
│   │   │   └── ProcessLogEntity.cs             # EF Core 实体
│   │   └── Repositories/
│   │       └── ProcessLogRepository.cs         # 处理日志仓储实现
│   └── Contract/Protos/
│       ├── mistake.proto                       # gRPC 契约定义
│       └── mistake.common.proto                # 公共消息定义 (ProcessLogDto)
└── docs/MistakeProcessLogging/GetMistakeProcessHistory/ # 本文档所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 关键接口签名和数据结构定义

### 领域模型

```csharp
// src/Domain/Models/ProcessLogModel.cs
public class ProcessLogModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MistakeItemId { get; set; } = Guid.Empty;
    public int Action { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<List<ProcessLogModel>> GetMistakeProcessHistoryAsync(Guid mistakeItemId)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc GetMistakeProcessHistory(IdRequest) returns (ProcessLogListResponse);

message IdRequest {
  string id = 1;  // 此处为 mistake_item_id
}

message ProcessLogListResponse {
  repeated ProcessLogDto logs = 1;
}

// src/Contract/Protos/mistake.common.proto
message ProcessLogDto {
  string id = 1;
  string mistake_item_id = 2;
  ProcessAction action = 3;
  string details = 4;
  string created_at = 5;
}
```

### 仓储接口

```csharp
// src/Domain/Repositories/IProcessLogRepository.cs
public interface IProcessLogRepository
{
    Task<List<ProcessLogModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
    // ...
}
```

### gRPC 层映射

```csharp
// src/Service/MistakeServiceImpl.cs
private static ProcessLogDto MapProcessLogDto(ProcessLogModel log)
{
    return new ProcessLogDto
    {
        Id = log.Id.ToString(),
        MistakeItemId = log.MistakeItemId.ToString(),
        Action = (ProcessAction)log.Action,
        Details = log.Details ?? string.Empty,
        CreatedAt = log.CreatedAt == DateTimeOffset.MinValue
            ? string.Empty
            : log.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
    };
}
```

## 数据流描述（步骤序列）

### 查询处理日志历史流程 (`GetMistakeProcessHistory`)

1. gRPC 层接收 `IdRequest`，将 `Id` 通过 `Guid.Parse` 解析为 GUID（作为 `mistakeItemId`）。
2. 调用 `MistakeDomainService.GetMistakeProcessHistoryAsync(mistakeItemId)`。
3. 领域服务调用 `_processLogRepository.GetByMistakeItemIdAsync(mistakeItemId)` 查询日志列表。
4. 返回 `List<ProcessLogModel>`。
5. gRPC 层通过 `MapProcessLogDto` 将每个 `ProcessLogModel` 映射为 `ProcessLogDto`。
6. 将映射后的 DTO 列表填入 `ProcessLogListResponse.Logs` 并返回。

## 错误处理策略

- **GUID 格式**：`Guid.Parse` 在传入非法格式时抛出 `FormatException`，由 gRPC 框架转为 Internal 错误。
- **错题无日志**：仓储返回空列表时，gRPC 层返回空的 `ProcessLogListResponse`。
- **仓储异常**：`GetByMistakeItemIdAsync` 异常向上抛出，由 gRPC 框架处理。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IProcessLogRepository` | 按错题 ID 查询日志列表 `GetByMistakeItemIdAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`IProcessLogRepository` 通过构造函数注入，可被 Moq 替换。
- **查询为只读**：不修改任何数据，测试只需验证返回列表的内容和数量。
- **空列表分支**：Mock `GetByMistakeItemIdAsync` 返回空列表验证返回空的 `ProcessLogListResponse`。