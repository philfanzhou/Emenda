# LogMistakeProcess - 设计文档

## 文件结构

| 文件 | 项目相对路径 |
|------|-------------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` |
| 领域模型 | `src/Domain/Models/ProcessLogModel.cs` |
| 仓储接口 | `src/Domain/Repositories/IProcessLogRepository.cs`, `IMistakeItemRepository.cs` |
| 仓储实现 | `src/Database/Repositories/ProcessLogRepository.cs`, `MistakeItemRepository.cs` |
| 数据实体 | `src/Database/Entities/ProcessLogEntity.cs` |

## 关键接口签名

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<BoolResponse> LogMistakeProcess(LogMistakeProcessRequest request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<bool> LogMistakeProcessAsync(Guid mistakeItemId, string studentId, ProcessAction action, string details)
```

### 仓储接口

```csharp
// IMistakeItemRepository.cs
Task<MistakeItemModel?> GetByIdAsync(Guid id);

// IProcessLogRepository.cs
Task AddAsync(ProcessLogModel model);
```

### 内部辅助方法

```csharp
// MistakeDomainService.cs (private)
private async Task AddProcessLogAsync(Guid mistakeItemId, ProcessAction action, string details)
```

## 依赖的数据库表

| 表名 | 说明 |
|------|------|
| `mistake_item` | 错题条目主表，用于验证存在性 |
| `process_log` | 操作日志表，存储日志记录 |

> 数据库 Schema 详情参见 `src/Database/MistakeDbContext.cs`。process_log 通过 `MistakeItemId` 外键关联 mistake_item，配置 `OnDelete(DeleteBehavior.Cascade)`。

## 数据流 / 调用链

```
Client
  │
  ▼
MistakeServiceImpl.LogMistakeProcess(request)
  │  1. Guid.Parse(request.MistakeItemId)
  │  2. 提取 request.StudentId, request.Action, request.Details
  │
  ▼
MistakeDomainService.LogMistakeProcessAsync(mistakeItemId, studentId, action, details)
  │  3. IMistakeItemRepository.GetByIdAsync(mistakeItemId)
  │     └─ null → return false
  │
  ▼
AddProcessLogAsync(mistakeItemId, action, details)
  │  4. 创建 ProcessLogModel:
  │     ├─ Id = Guid.NewGuid()
  │     ├─ MistakeItemId = mistakeItemId
  │     ├─ Action = (int)action
  │     ├─ Details = details
  │     └─ CreatedAt = DateTimeOffset.UtcNow
  │
  ▼
IProcessLogRepository.AddAsync(processLog)
  │  5. Model → Entity 映射
  │  6. DbContext.ProcessLogs.AddAsync(entity)
  │
  ▼
IUnitOfWork.SaveChangesAsync()
  │  7. 提交事务
  │
  ▼
MistakeServiceImpl: BoolResponse { Success = result }
  │
  ▼
Client ← BoolResponse
```

## 关键设计决策

1. **studentId 未使用**：`LogMistakeProcessAsync` 接受 `studentId` 参数但当前未存储到 ProcessLog 中，预留扩展空间。
2. **复用 AddProcessLogAsync**：内部使用与 AddMistakeItem/AddPractice 相同的 `AddProcessLogAsync` 方法，保证日志格式一致。
3. **不存在时返回 false**：与 DeleteMistakeItem 一致，不抛异常，返回布尔值表示操作结果。
4. **Action 存储为 int**：ProcessLogModel.Action 为 int 类型，对应 ProcessAction 枚举的整数值。
