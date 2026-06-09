# ReviewMistakeItem — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── MistakeItemModel.cs              # 错题条目领域模型（包含 ReviewStatus/ReviewerId/ReviewedAt/ReviewComment/Grade/Subject）
│   │   ├── Services/
│   │   │   └── MistakeDomainService.cs          # 领域服务 (ReviewMistakeItemAsync)
│   │   └── Repositories/
│   │       └── IMistakeItemRepository.cs        # 错题仓储接口 (GetByIdAsync/UpdateAsync)
│   ├── Database/
│   │   ├── Entities/
│   │   │   └── MistakeItemEntity.cs             # EF Core 实体
│   │   └── Repositories/
│   │       └── MistakeItemRepository.cs         # 错题仓储实现
│   ├── Service/
│   │   └── MistakeServiceImpl.cs                # gRPC 实现 (ReviewMistakeItem)
│   └── Contract/Protos/
│       ├── mistake.proto                        # gRPC 契约定义 (ReviewMistakeItemRequest/BoolResponse)
│       └── mistake.common.proto                 # 公共类型 (ReviewStatus 枚举)
├── test/
│   └── (待创建)                                   # 单元测试
└── docs/MistakeReview/ReviewMistakeItem/         # 本文档所在目录
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
    public int Subject { get; set; }                     // 学科 1-9
    public int Grade { get; set; }                        // 年级 1-12
    public string SourceUploadId { get; set; } = string.Empty;
    public List<SourceRegionModel> SourceRegions { get; set; } = new();
    public int ReviewStatus { get; set; }                 // 对应 ReviewStatus 枚举值
    public string? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public int Type { get; set; }
    public string? QuestionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<MistakeItemModel?> ReviewMistakeItemAsync(
    Guid id,
    int reviewStatus,
    string? reviewerId,
    string? reviewComment,
    int? grade = null,
    int? subject = null)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc ReviewMistakeItem(ReviewMistakeItemRequest) returns (BoolResponse);

message ReviewMistakeItemRequest {
  string id = 1;
  ReviewStatus review_status = 2;
  string reviewer_id = 3;
  string review_comment = 4;
  int32 grade = 5;       // 可选，>0 时生效
  int32 subject = 6;     // 可选，>0 时生效
}

// ReviewStatus 枚举（来自 mistake.common.proto）
enum ReviewStatus {
  REVIEW_STATUS_UNSPECIFIED = 0;
  REVIEW_STATUS_PENDING_REVIEW = 1;
  REVIEW_STATUS_CONFIRMED = 2;
  REVIEW_STATUS_REJECTED = 3;
}
```

### 仓储接口

```csharp
// src/Domain/Repositories/IMistakeItemRepository.cs
public interface IMistakeItemRepository
{
    Task<MistakeItemModel?> GetByIdAsync(Guid id);
    Task<bool> UpdateAsync(MistakeItemModel model);
    // ... 其他方法
}
```

### 注入的外部依赖

```csharp
public MistakeDomainService(
    IMistakeItemRepository mistakeItemRepository,
    IMistakePracticeRepository mistakePracticeRepository,
    IProcessLogRepository processLogRepository,
    IUnitOfWork unitOfWork,
    ILogger<MistakeDomainService> logger)
```

- `IMistakeItemRepository`：错题条目仓储，提供 `GetByIdAsync` / `UpdateAsync`
- `IProcessLogRepository`：操作日志仓储，提供 `AddAsync`
- `IUnitOfWork`：事务控制，提供 `SaveChangesAsync`

## 数据流描述（步骤序列）

### 错题审核流程 (`ReviewMistakeItem`)

1. gRPC 层接收 `ReviewMistakeItemRequest`，校验 `review_status` 必须是 `Confirmed` 或 `Rejected`，否则抛出 `RpcException(StatusCode.InvalidArgument, "审核结果只能是确认或驳回")`。
2. gRPC 层处理可选参数：`grade > 0` 时透传，否则传 `null`；`subject > 0` 时透传，否则传 `null`。
3. 调用 `MistakeDomainService.ReviewMistakeItemAsync(id, reviewStatus, reviewerId, reviewComment, grade, subject)`。
4. 领域服务通过 `_mistakeItemRepository.GetByIdAsync(id)` 获取错题；若不存在返回 `null`。
5. 校验错题当前 `ReviewStatus == (int)ReviewStatus.PendingReview`，否则抛出 `MistakeValidationException("只能审核待审核状态的错题")`。
6. 二次校验 `reviewStatus` 为 `Confirmed` 或 `Rejected`，否则抛出验证异常。
7. 更新错题字段：`ReviewStatus = reviewStatus`、`ReviewerId = reviewerId`、`ReviewedAt = DateTimeOffset.UtcNow`、`ReviewComment = reviewComment`、`UpdatedAt = DateTimeOffset.UtcNow`。
8. 若 `grade` 有效（>0 且 ≤12），更新 `item.Grade`。
9. 若 `subject` 有效（>0 且 ≤9），更新 `item.Subject`。
10. 调用 `_mistakeItemRepository.UpdateAsync(item)`。
11. 根据审核结果写入操作日志：确认为 `ProcessAction.ReviewConfirmed`，驳回为 `ProcessAction.ReviewRejected`。
12. 调用 `_unitOfWork.SaveChangesAsync()` 提交事务。
13. 返回更新后的 `MistakeItemModel`。
14. gRPC 层判断返回值：非 null 返回 `BoolResponse { Success = true }`，null 返回 `BoolResponse { Success = false }`。

## 错误处理策略

- **gRPC 层前置校验**：审核结果值非法直接在 gRPC 层抛出 `RpcException(StatusCode.InvalidArgument)`，不进入领域服务。
- **领域服务状态守卫**：错题非待审核状态抛出 `MistakeValidationException`，由 gRPC 层捕获转换为 `RpcException(StatusCode.InvalidArgument)`。
- **事务保护**：错题状态更新与操作日志写入在同一事务中提交，确保原子性。
- **乐观并发**：`updated_at` 字段为后续并发控制提供基础。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 错题条目 GetByIdAsync / UpdateAsync | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IProcessLogRepository` | 操作日志 AddAsync | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository`、`IProcessLogRepository`、`IUnitOfWork` 全部通过构造函数注入，可被 Moq 替换。
- **状态守卫可触发**：Moq 返回指定 `ReviewStatus` 的错题模型，可测试各种状态转换场景。
- **时间可替换**：测试不依赖精确时间戳，只做范围断言。
- **失败路径可触发**：Moq `ThrowsAsync` 可模拟仓储/事务失败；Moq null 返回值可模拟错题不存在。