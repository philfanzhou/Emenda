# DeleteMistakeItem — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── MistakeItemModel.cs              # 错题条目领域模型
│   │   │   ├── MistakePracticeModel.cs          # 练习记录模型
│   │   │   └── ProcessLogModel.cs               # 操作日志模型
│   ├── Services/
│   │   └── MistakeDomainService.cs              # 领域服务 (DeleteMistakeItemAsync)
│   ├── Repositories/
│   │   ├── IMistakeItemRepository.cs            # 错题条目仓储接口 (GetByIdAsync, DeleteAsync)
│   │   ├── IMistakePracticeRepository.cs        # 练习记录仓储接口 (RemoveByMistakeItemIdAsync)
│   │   └── IProcessLogRepository.cs             # 操作日志仓储接口 (RemoveByMistakeItemIdAsync)
│   ├── Database/
│   │   └── Repositories/
│   │       ├── MistakeItemRepository.cs         # 错题条目仓储实现
│   │       ├── MistakePracticeRepository.cs     # 练习记录仓储实现
│   │       └── ProcessLogRepository.cs          # 操作日志仓储实现
│   ├── Service/
│   │   └── MistakeServiceImpl.cs                # gRPC 实现 (DeleteMistakeItem)
│   └── Contract/Protos/
│       ├── mistake.proto                        # gRPC 契约定义 (DeleteMistakeItem)
│       └── mistake.common.proto                 # 公共消息定义 (IdRequest, BoolResponse)
├── test/
│   └── Ruoyu.Study.Mistake.Domain.Tests/
│       └── MistakeDomainServiceTests.cs         # 单元测试 (DeleteMistakeItemAsync_*)
└── docs/MistakeManagement/DeleteMistakeItem/    # 本文档所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 关键接口签名和数据结构定义

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<bool> DeleteMistakeItemAsync(Guid id)
```

### 仓储接口（相关方法）

```csharp
// src/Domain/Repositories/IMistakeItemRepository.cs
Task<MistakeItemModel?> GetByIdAsync(Guid id);
Task<bool> DeleteAsync(Guid id);

// src/Domain/Repositories/IMistakePracticeRepository.cs
Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);

// src/Domain/Repositories/IProcessLogRepository.cs
Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc DeleteMistakeItem(IdRequest) returns (BoolResponse);

// src/Contract/Protos/mistake.common.proto
message IdRequest {
  string id = 1;
}

message BoolResponse {
  bool success = 1;
  optional string error_message = 2;
}
```

### gRPC 层实现逻辑

```csharp
// src/Service/MistakeServiceImpl.cs
public override async Task<BoolResponse> DeleteMistakeItem(IdRequest request, ServerCallContext context)
{
    try
    {
        var success = await _mistakeService.DeleteMistakeItemAsync(Guid.Parse(request.Id));
        return new BoolResponse { Success = success };
    }
    catch (MistakeValidationException ex)
    {
        throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
    }
}
```

### 注入的外部依赖

```csharp
public MistakeDomainService(
    IMistakeItemRepository mistakeItemRepository,       // GetByIdAsync, DeleteAsync
    IMistakePracticeRepository mistakePracticeRepository, // RemoveByMistakeItemIdAsync
    IProcessLogRepository processLogRepository,          // RemoveByMistakeItemIdAsync
    IUnitOfWork unitOfWork,                               // SaveChangesAsync
    ILogger<MistakeDomainService> logger)
```

## 数据流描述（步骤序列）

### 删除错题条目流程 (`DeleteMistakeItem`)

1. gRPC 层接收 `IdRequest`，解析 ID 为 `Guid`。
2. 调用 `MistakeDomainService.DeleteMistakeItemAsync(id)`。
3. 领域服务内部：
   a. 调用 `_mistakeItemRepository.GetByIdAsync(id)` 查找条目。
   b. 若条目不存在 → 返回 `false`。
   c. 若条目存在 → 依次执行：
      - `_mistakePracticeRepository.RemoveByMistakeItemIdAsync(id)` — 删除关联练习记录
      - `_processLogRepository.RemoveByMistakeItemIdAsync(id)` — 删除关联操作日志
      - `_mistakeItemRepository.DeleteAsync(id)` — 删除错题条目自身
   d. 调用 `_unitOfWork.SaveChangesAsync()` 提交事务。
   e. 返回 `true`。
4. gRPC 层将 bool 结果包装为 `BoolResponse { Success = success }` 返回。
5. 若领域服务抛出 `MistakeValidationException`，gRPC 层转换为 `RpcException(StatusCode.InvalidArgument)`。

## 错误处理策略

- **条目不存在**：返回 `false` 而非抛异常，调用方通过 `BoolResponse.Success` 判断结果。
- **事务保护**：三级删除（练习记录 → 操作日志 → 错题条目）由 `IUnitOfWork.SaveChangesAsync` 一次性提交，避免部分删除。
- **不限制审核状态**：任意 `ReviewStatus`（PendingReview、Confirmed、Rejected）的条目均可被删除，无前置状态检查。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 错题条目 GetByIdAsync / DeleteAsync | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IMistakePracticeRepository` | 练习记录 RemoveByMistakeItemIdAsync | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IProcessLogRepository` | 操作日志 RemoveByMistakeItemIdAsync | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：四个外部依赖全部通过构造函数注入，可被 Moq 替换。
- **条目不存在可测**：Mock `GetByIdAsync` 返回 null，验证返回 false。
- **删除顺序可测**：通过 Moq `Verify` 按顺序验证三次删除操作的调用。
- **事务失败可测**：Mock `SaveChangesAsync` 抛异常，验证异常向上传播。