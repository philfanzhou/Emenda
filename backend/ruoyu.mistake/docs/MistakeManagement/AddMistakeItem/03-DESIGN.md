# AddMistakeItem — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── MistakeItemModel.cs              # 错题条目领域模型
│   │   │   ├── SourceRegionModel.cs             # 来源图片区域模型
│   │   │   ├── BoundingBoxModel.cs              # 标注框模型
│   │   │   └── ProcessLogModel.cs               # 操作日志模型
│   ├── Services/
│   │   └── MistakeDomainService.cs              # 领域服务 (AddMistakeItemAsync, ValidateMistakeItemData)
│   ├── Repositories/
│   │   ├── IMistakeItemRepository.cs            # 错题条目仓储接口
│   │   └── IProcessLogRepository.cs             # 操作日志仓储接口
│   ├── Database/
│   │   ├── Entities/
│   │   │   ├── MistakeItemEntity.cs             # EF Core 实体
│   │   │   └── ProcessLogEntity.cs              # EF Core 操作日志实体
│   │   └── Repositories/
│   │       ├── MistakeItemRepository.cs         # 错题条目仓储实现
│   │       └── ProcessLogRepository.cs          # 操作日志仓储实现
│   ├── Service/
│   │   └── MistakeServiceImpl.cs                # gRPC 实现 (AddMistakeItem)
│   └── Contract/Protos/
│       ├── mistake.proto                        # gRPC 契约定义 (AddMistakeItem, AddMistakeItemRequest)
│       └── mistake.common.proto                 # 公共消息定义 (MistakeItemDto, SourceRegion, BoundingBox, ReviewStatus, ProcessAction)
├── test/
│   └── Ruoyu.Study.Mistake.Domain.Tests/
│       └── MistakeDomainServiceTests.cs         # 单元测试 (AddMistakeItemAsync_WithSourceRegions_ShouldSucceed)
└── docs/MistakeManagement/AddMistakeItem/       # 本文档所在目录
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
// src/Domain/Models/MistakeItemModel.cs
public class MistakeItemModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public string SourceUploadId { get; set; } = string.Empty;
    public List<SourceRegionModel> SourceRegions { get; set; } = new();
    public int ReviewStatus { get; set; }
    public string? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public int Type { get; set; }
    public string? QuestionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

// src/Domain/Models/SourceRegionModel.cs (定义在 MistakeItemModel.cs 同文件)
public class SourceRegionModel
{
    public string SourceImagePath { get; set; } = string.Empty;
    public BoundingBoxModel? BoundingBox { get; set; }
}

// src/Domain/Models/BoundingBoxModel.cs (定义在 MistakeItemModel.cs 同文件)
public class BoundingBoxModel
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<MistakeItemModel> AddMistakeItemAsync(MistakeItemModel item)

// 内部校验方法
private static void ValidateMistakeItemData(MistakeItemModel item)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc AddMistakeItem(AddMistakeItemRequest) returns (MistakeItemDto);

message AddMistakeItemRequest {
  string student_id = 1;
  int32 subject = 2;
  int32 grade = 3;
  string source_upload_id = 4;
  repeated SourceRegion source_regions = 5;
  string comments = 6;
}

// src/Contract/Protos/mistake.common.proto
message SourceRegion {
  string source_image_path = 1;
  BoundingBox bounding_box = 2;
}

message BoundingBox {
  int32 x1 = 1;
  int32 y1 = 2;
  int32 x2 = 3;
  int32 y2 = 4;
}

enum ReviewStatus {
  REVIEW_STATUS_UNSPECIFIED = 0;
  REVIEW_STATUS_PENDING_REVIEW = 1;
  REVIEW_STATUS_CONFIRMED = 2;
  REVIEW_STATUS_REJECTED = 3;
}

enum ProcessAction {
  PROCESS_ACTION_UNSPECIFIED = 0;
  PROCESS_ACTION_CREATED = 1;
  // ...
}
```

### 注入的外部依赖

```csharp
// 构造函数签名
public MistakeDomainService(
    IMistakeItemRepository mistakeItemRepository,
    IMistakePracticeRepository mistakePracticeRepository,
    IProcessLogRepository processLogRepository,
    IUnitOfWork unitOfWork,
    ILogger<MistakeDomainService> logger)
```

- `IMistakeItemRepository`：错题条目表仓储，提供 `AddAsync`
- `IProcessLogRepository`：操作日志表仓储，提供 `AddAsync`
- `IUnitOfWork`：事务控制，提供 `SaveChangesAsync`

## 数据流描述（步骤序列）

### 手动添加错题条目流程 (`AddMistakeItem`)

1. gRPC 层接收 `AddMistakeItemRequest`，将 proto 消息映射为 `MistakeItemModel`：
   - 直接映射 `StudentId`、`Subject`、`Grade`、`SourceUploadId`
   - 将 `request.SourceRegions` 映射为 `List<SourceRegionModel>`，每个 region 包含 `SourceImagePath` 和可选的 `BoundingBoxModel`
2. 调用 `MistakeDomainService.AddMistakeItemAsync(item)`。
3. 领域服务内部调用 `ValidateMistakeItemData(item)` 进行参数校验：
   - student_id 非空
   - subject 在 0-9 范围内
   - grade 在 0-12 范围内
   - source_upload_id 非空
   - source_regions 非空
4. 校验通过后，设置 `item.Id = Guid.NewGuid()`、`item.ReviewStatus = (int)ReviewStatus.PendingReview`、`item.CreatedAt = DateTimeOffset.UtcNow`。
5. 调用 `_mistakeItemRepository.AddAsync(item)` 写入错题条目。
6. 调用 `AddProcessLogAsync(item.Id, ProcessAction.Created, "Mistake item manually added by teacher")` 写入操作日志。
7. 调用 `_unitOfWork.SaveChangesAsync()` 提交事务。
8. 返回完整的 `MistakeItemModel`。
9. gRPC 层将模型通过 `MapMistakeItemDto` 转换为 `MistakeItemDto` 返回。

## 错误处理策略

- **参数校验**：在领域服务层通过 `ValidateMistakeItemData` 进行校验，无效参数抛出 `MistakeValidationException`；gRPC 层 catch 后转换为 `RpcException(StatusCode.InvalidArgument)`。
- **事务保护**：错题条目与操作日志的写入由 `IUnitOfWork.SaveChangesAsync` 一次性提交，避免部分写入。
- **BoundingBox 可选**：`BoundingBoxModel?` 可为 null，表示整张图片区域；gRPC 映射时若 `request.BoundingBox == null` 则设为 null。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 错题条目 Add/GetById/Update/Delete/GetBySourceUploadId/GetList | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IProcessLogRepository` | 操作日志 Add/GetByMistakeItemId/RemoveByMistakeItemId | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository`、`IProcessLogRepository`、`IUnitOfWork` 全部通过构造函数注入，可被 Moq 替换。
- **时间可替换**：当前代码直接使用 `DateTimeOffset.UtcNow`；测试不依赖精确时间戳，只做范围断言。
- **失败路径可触发**：Moq `ThrowsAsync` 可模拟仓储/事务失败，验证回滚逻辑。
- **校验逻辑可独立测试**：`ValidateMistakeItemData` 为静态方法，可通过构造不同 MistakeItemModel 实例直接测试。