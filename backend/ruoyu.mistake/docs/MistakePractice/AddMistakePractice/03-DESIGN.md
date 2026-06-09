# AddMistakePractice — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── MistakePracticeModel.cs         # 错题练习领域模型
│   │   │   ├── MistakeItemModel.cs             # 错题条目领域模型（依赖）
│   │   │   └── ProcessLogModel.cs              # 操作日志模型
│   ├── Services/
│   │   └── MistakeDomainService.cs             # 领域服务 (AddPracticeAsync)
│   ├── Repositories/
│   │   ├── IMistakePracticeRepository.cs       # 错题练习仓储接口
│   │   ├── IMistakeItemRepository.cs           # 错题条目仓储接口（依赖）
│   │   ├── IProcessLogRepository.cs            # 操作日志仓储接口
│   │   └── IUnitOfWork.cs                      # 工作单元接口
│   ├── Database/
│   │   ├── Entities/
│   │   │   └── MistakePracticeEntity.cs        # EF Core 实体
│   │   └── Repositories/
│   │       └── MistakePracticeRepository.cs    # 错题练习仓储实现
│   ├── Service/
│   │   └── MistakeServiceImpl.cs               # gRPC 实现 (AddMistakePractice)
│   └── Contract/Protos/
│       ├── mistake.proto                       # gRPC 契约：AddMistakePractice 定义
│       └── mistake.common.proto                # gRPC 公共类型：MistakePracticeDto
├── test/
│   └── Ruoyu.Study.Mistake.Domain.Tests/
│       └── MistakeDomainServiceTests.cs        # 单元测试
└── docs/MistakePractice/AddMistakePractice/    # 本文档所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

PostgreSQL 表 `mistake_practice` 列：`id (uuid, PK)`, `mistake_item_id (uuid, FK → mistake_item.id)`, `student_id (varchar(36), NOT NULL)`, `practice_date (timestamptz)`, `image_paths (jsonb)`, `student_answer (text)`, `is_correct (bool)`, `comments (text)`。

## 关键接口签名和数据结构定义

### 领域模型

```csharp
// src/Domain/Models/MistakePracticeModel.cs
public class MistakePracticeModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MistakeItemId { get; set; } = Guid.Empty;
    public string StudentId { get; set; } = string.Empty;
    public DateTimeOffset PracticeDate { get; set; } = DateTimeOffset.UtcNow;
    public List<string> ImagePaths { get; set; } = new();
    public string? StudentAnswer { get; set; }
    public bool IsCorrect { get; set; }
    public string? Comments { get; set; }
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<MistakePracticeModel> AddPracticeAsync(
    MistakePracticeModel practice, List<string>? imagePaths = null)
```

- 方法内部通过 `practice.MistakeItemId` 查询对应的 `MistakeItemModel`。
- 校验错题条目状态为 `Confirmed` 后才允许添加练习。
- 若 `imagePaths` 非空，则覆盖 `practice.ImagePaths`。
- 返回创建成功的 `MistakePracticeModel`（含新生成的 `Id`）。

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc AddMistakePractice(AddMistakePracticeRequest) returns (MistakePracticeDto);

message AddMistakePracticeRequest {
  string mistake_item_id = 1;
  string student_id = 2;
  repeated string image_paths = 3;
  string student_answer = 4;
  bool is_correct = 5;
  string comments = 6;
}

// mistake.common.proto
message MistakePracticeDto {
  string id = 1;
  string mistake_item_id = 2;
  string student_id = 3;
  string practice_date = 4;
  repeated string image_paths = 5;
  string student_answer = 6;
  bool is_correct = 7;
  string comments = 8;
}
```

### 仓储接口

```csharp
// src/Domain/Repositories/IMistakePracticeRepository.cs
public interface IMistakePracticeRepository
{
    Task AddAsync(MistakePracticeModel model);
    Task<List<MistakePracticeModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
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

## 数据流描述（步骤序列）

### 添加错题练习流程 (`AddMistakePractice`)

1. gRPC 层接收 `AddMistakePracticeRequest`，构建 `MistakePracticeModel`：解析 `MistakeItemId` 为 `Guid`、赋值 `StudentId`、`StudentAnswer`、`IsCorrect`、`Comments`。
2. 若 `request.ImagePaths` 非空，构建 `imagePaths` 列表传入领域服务。
3. 调用 `MistakeDomainService.AddPracticeAsync(practice, imagePaths)`。
4. 领域服务通过 `_mistakeItemRepository.GetByIdAsync(practice.MistakeItemId)` 查询错题条目。
5. 若错题条目不存在，抛出 `MistakeValidationException("错题条目不存在")`。
6. 若 `item.ReviewStatus != (int)ReviewStatus.Confirmed`，抛出 `MistakeValidationException("只能对已确认的错题进行练习")`。
7. 分配新 `Guid` 作为练习记录 `Id`，设置 `PracticeDate = DateTimeOffset.UtcNow`。
8. 若 `imagePaths` 非空，写入 `practice.ImagePaths`。
9. 调用 `_mistakePracticeRepository.AddAsync(practice)` 写入练习记录。
10. 调用 `AddProcessLogAsync(item.Id, ProcessAction.Practiced, "Student practiced mistake")` 写入操作日志。
11. 调用 `_unitOfWork.SaveChangesAsync()` 提交事务。
12. 返回 `practice`。
13. gRPC 层将 `MistakePracticeModel` 转换为 `MistakePracticeDto` 返回。

## 错误处理策略

- **前置校验**：领域服务先查询错题条目，不存在或状态不符时抛出 `MistakeValidationException`。
- **异常转换**：gRPC 层捕获 `MistakeValidationException` 并转换为 `RpcException(StatusCode.InvalidArgument)`。
- **事务保护**：练习记录与操作日志的写入由 `IUnitOfWork.SaveChangesAsync` 一次性提交，避免部分写入。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 错题条目 GetById/Add/Update/Delete 等 | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IMistakePracticeRepository` | 错题练习 Add/GetByMistakeItemId/Remove 等 | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IProcessLogRepository` | 操作日志 Add/GetByMistakeItemId/Remove | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.Mistake.Domain` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository`、`IMistakePracticeRepository`、`IProcessLogRepository`、`IUnitOfWork` 全部通过构造函数注入，可被 Moq 替换。
- **时间可替换**：当前代码直接使用 `DateTimeOffset.UtcNow`；测试不依赖精确时间戳，只做范围断言。
- **状态驱动测试**：通过 mock 不同的 `item.ReviewStatus` 值，可验证 "已确认" 和 "非已确认" 两条分支路径。
- **失败路径可触发**：Moq `ReturnsAsync(null)` 模拟错题不存在；Moq `ThrowsAsync` 模拟事务失败。