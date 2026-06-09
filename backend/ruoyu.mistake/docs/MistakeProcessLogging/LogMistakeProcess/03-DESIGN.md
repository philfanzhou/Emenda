# LogMistakeProcess — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── ProcessLogModel.cs               # 处理日志领域模型
│   │   ├── Services/
│   │   │   └── MistakeDomainService.cs          # 领域服务 (LogMistakeProcessAsync)
│   │   └── Repositories/
│   │       ├── IMistakeItemRepository.cs        # 错题条目仓储接口
│   │       ├── IProcessLogRepository.cs         # 处理日志仓储接口
│   │       └── IUnitOfWork.cs                   # 事务工作单元接口
│   ├── Service/
│   │   └── MistakeServiceImpl.cs               # gRPC 实现 (LogMistakeProcess)
│   ├── Database/
│   │   ├── Entities/
│   │   │   └── ProcessLogEntity.cs             # EF Core 实体
│   │   └── Repositories/
│   │       └── ProcessLogRepository.cs         # 处理日志仓储实现
│   └── Contract/Protos/
│       ├── mistake.proto                       # gRPC 契约定义
│       └── mistake.common.proto                # 公共消息定义 (ProcessAction, ProcessLogDto)
└── docs/MistakeProcessLogging/LogMistakeProcess/ # 本文档所在目录
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
public async Task<bool> LogMistakeProcessAsync(
    Guid mistakeItemId, string studentId, ProcessAction action, string details)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc LogMistakeProcess(LogMistakeProcessRequest) returns (BoolResponse);

message LogMistakeProcessRequest {
  string mistake_item_id = 1;
  string student_id = 2;
  ProcessAction action = 3;
  string details = 4;
}
```

### ProcessAction 枚举

```protobuf
// src/Contract/Protos/mistake.common.proto
enum ProcessAction {
  PROCESS_ACTION_UNSPECIFIED = 0;
  PROCESS_ACTION_CREATED = 1;
  PROCESS_ACTION_ANALYZED = 2;
  PROCESS_ACTION_REVIEW_CONFIRMED = 3;
  PROCESS_ACTION_REVIEW_REJECTED = 4;
  PROCESS_ACTION_PRACTICED = 5;
  PROCESS_ACTION_MASTERED = 6;
}
```

### 仓储接口

```csharp
// src/Domain/Repositories/IProcessLogRepository.cs
public interface IProcessLogRepository
{
    Task AddAsync(ProcessLogModel model);
    // ...
}
```

## 数据流描述（步骤序列）

### 记录处理日志流程 (`LogMistakeProcess`)

1. gRPC 层接收 `LogMistakeProcessRequest`，将 `MistakeItemId` 通过 `Guid.Parse` 解析为 GUID。
2. 调用 `MistakeDomainService.LogMistakeProcessAsync(mistakeItemId, studentId, action, details)`。
3. 领域服务通过 `_mistakeItemRepository.GetByIdAsync` 验证错题条目是否存在。
4. 若条目不存在，直接返回 `false`，不写入任何数据。
5. 若条目存在，调用私有方法 `AddProcessLogAsync`：
   - 创建 `ProcessLogModel`，分配新 GUID 作为 `Id`，设置 `MistakeItemId`、`Action`（转为 int）、`Details`、`CreatedAt = DateTimeOffset.UtcNow`。
   - 调用 `_processLogRepository.AddAsync(processLog)` 写入仓储。
6. 调用 `_unitOfWork.SaveChangesAsync` 提交事务。
7. 返回 `true`。

### 私有辅助方法 `AddProcessLogAsync`

```csharp
private async Task AddProcessLogAsync(Guid mistakeItemId, ProcessAction action, string details)
{
    var processLog = new ProcessLogModel
    {
        Id = Guid.NewGuid(),
        MistakeItemId = mistakeItemId,
        Action = (int)action,
        Details = details,
        CreatedAt = DateTimeOffset.UtcNow
    };
    await _processLogRepository.AddAsync(processLog);
}
```

## 错误处理策略

- **错题不存在**：`GetByIdAsync` 返回 null 时，返回 `false`，不抛异常。
- **GUID 格式**：`Guid.Parse` 在传入非法格式时抛出 `FormatException`，由 gRPC 框架转为 Internal 错误。
- **事务失败**：`SaveChangesAsync` 异常向上抛出，由 gRPC 框架处理。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 通过 ID 查询错题条目 `GetByIdAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IProcessLogRepository` | 写入处理日志 `AddAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.Mistake.Domain` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository`、`IProcessLogRepository`、`IUnitOfWork` 全部通过构造函数注入，可被 Moq 替换。
- **错题不存在分支**：Mock `GetByIdAsync` 返回 null 验证返回 `false`。
- **成功路径**：Mock 验证 `AddAsync` 和 `SaveChangesAsync` 被调用一次，且传入的模型字段正确。
- **时间可替换**：不依赖精确时间戳，只做范围断言（±5s）。