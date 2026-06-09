# UpdateMistakeItem — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── MistakeItemModel.cs              # 错题条目领域模型
│   ├── Services/
│   │   └── MistakeDomainService.cs              # 领域服务 (UpdateMistakeItemAsync)
│   ├── Repositories/
│   │   └── IMistakeItemRepository.cs            # 错题条目仓储接口 (UpdateAsync, GetByIdAsync)
│   ├── Database/
│   │   └── Repositories/
│   │       └── MistakeItemRepository.cs         # 错题条目仓储实现
│   ├── Service/
│   │   └── MistakeServiceImpl.cs                # gRPC 实现 (UpdateMistakeItem)
│   └── Contract/Protos/
│       ├── mistake.proto                        # gRPC 契约定义 (UpdateMistakeItem, UpdateMistakeItemRequest)
│       └── mistake.common.proto                 # 公共消息定义 (MistakeItemDto, SourceRegion, BoundingBox)
├── test/
│   └── Ruoyu.Study.Mistake.Domain.Tests/
│       └── MistakeDomainServiceTests.cs         # 单元测试 (UpdateMistakeItemAsync_ShouldUpdateItem)
└── docs/MistakeManagement/UpdateMistakeItem/    # 本文档所在目录
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
public async Task<bool> UpdateMistakeItemAsync(MistakeItemModel model)

public async Task<MistakeItemModel?> GetMistakeItemAsync(Guid id)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc UpdateMistakeItem(UpdateMistakeItemRequest) returns (MistakeItemDto);

message UpdateMistakeItemRequest {
  string id = 1;
  string student_id = 2;
  int32 subject = 3;
  int32 grade = 4;
  repeated SourceRegion source_regions = 5;
}
```

### gRPC 层实现逻辑

```csharp
// src/Service/MistakeServiceImpl.cs
public override async Task<MistakeItemDto> UpdateMistakeItem(UpdateMistakeItemRequest request, ServerCallContext context)
{
    // 1. 先查询条目
    var item = await _mistakeService.GetMistakeItemAsync(Guid.Parse(request.Id));
    if (item == null)
        throw new RpcException(new Status(StatusCode.NotFound, "错题条目不存在"));

    // 2. 部分更新 - 仅更新非空/非零字段
    if (!string.IsNullOrWhiteSpace(request.StudentId))
        item.StudentId = request.StudentId;
    if (request.Subject > 0)
        item.Subject = request.Subject;
    if (request.Grade > 0)
        item.Grade = request.Grade;
    if (request.SourceRegions.Count > 0)
    {
        item.SourceRegions = request.SourceRegions.Select(r => new SourceRegionModel
        {
            SourceImagePath = r.SourceImagePath,
            BoundingBox = r.BoundingBox != null
                ? new BoundingBoxModel { X1 = ..., Y1 = ..., X2 = ..., Y2 = ... }
                : null
        }).ToList();
    }

    // 3. 调用领域服务更新
    var success = await _mistakeService.UpdateMistakeItemAsync(item);
    if (!success)
        throw new RpcException(new Status(StatusCode.Internal, "更新失败"));

    // 4. 重新查询并返回更新后的条目
    var updatedItem = await _mistakeService.GetMistakeItemAsync(Guid.Parse(request.Id));
    return MapMistakeItemDto(updatedItem);
}
```

### 注入的外部依赖

```csharp
public MistakeDomainService(
    IMistakeItemRepository mistakeItemRepository,      // 提供 UpdateAsync, GetByIdAsync
    IMistakePracticeRepository mistakePracticeRepository,
    IProcessLogRepository processLogRepository,
    IUnitOfWork unitOfWork,                             // 提供 SaveChangesAsync
    ILogger<MistakeDomainService> logger)
```

## 数据流描述（步骤序列）

### 更新错题条目流程 (`UpdateMistakeItem`)

1. gRPC 层接收 `UpdateMistakeItemRequest`，解析 ID 为 `Guid`。
2. 调用 `MistakeDomainService.GetMistakeItemAsync(id)` 查找条目。若返回 null，抛出 `RpcException(StatusCode.NotFound, "错题条目不存在")`。
3. 在内存中对 `MistakeItemModel` 应用部分更新：
   - `student_id` 非空 → 更新 `StudentId`
   - `subject > 0` → 更新 `Subject`
   - `grade > 0` → 更新 `Grade`
   - `source_regions.Count > 0` → 映射为新 `List<SourceRegionModel>`（含 `BoundingBoxModel` 可选映射）
4. 调用 `MistakeDomainService.UpdateMistakeItemAsync(item)`。
5. 领域服务内部调用 `_mistakeItemRepository.UpdateAsync(model)`，成功后调用 `_unitOfWork.SaveChangesAsync()`。返回 bool。
6. gRPC 层检查返回值：`false` 则抛出 `RpcException(StatusCode.Internal, "更新失败")`。
7. 重新调用 `GetMistakeItemAsync(id)` 获取最新数据，通过 `MapMistakeItemDto` 转换为 `MistakeItemDto` 返回。

## 错误处理策略

- **条目不存在**：gRPC 层查询结果为 null 时，抛出 `RpcException(StatusCode.NotFound)`。
- **更新失败**：领域服务 `UpdateAsync` 返回 false 时，gRPC 层抛出 `RpcException(StatusCode.Internal)`。
- **参数校验**：更新操作本身不做字段值域校验（如 subject 0-9），仅检查非空/非零以决定是否覆盖。
- **BoundingBox 可选**：映射时 `BoundingBox == null` 则 `BoundingBoxModel = null`。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 错题条目 UpdateAsync / GetByIdAsync | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository`、`IUnitOfWork` 全部通过构造函数注入，可被 Moq 替换。
- **先查后改模式可测**：Mock `GetByIdAsync` 返回 null 或有效对象，验证 NotFound 和成功路径。
- **UpdateAsync 失败可测**：Mock `UpdateAsync` 返回 false，验证 Internal 错误路径。
- **部分更新逻辑在 gRPC 层**：gRPC 层可独立测试部分更新字段覆盖逻辑，与领域服务解耦。