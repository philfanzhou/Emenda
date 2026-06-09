# SubmitMistakeUpload — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── MistakeItemModel.cs            # 错题条目领域模型
│   │   │   ├── SourceRegionModel.cs           # 图片来源区域模型
│   │   │   ├── ProcessLogModel.cs             # 操作日志模型
│   │   │   └── BoundingBoxModel.cs            # 边界框模型
│   ├── Services/
│   │   └── MistakeDomainService.cs            # 领域服务 (SubmitMistakeUploadAsync)
│   ├── Repositories/
│   │   ├── IMistakeItemRepository.cs          # 错题条目仓储接口
│   │   ├── IProcessLogRepository.cs           # 操作日志仓储接口
│   │   └── IUnitOfWork.cs                     # 工作单元接口
│   ├── Database/
│   │   ├── Entities/
│   │   │   └── MistakeItemEntity.cs           # EF Core 实体
│   │   └── Repositories/
│   │       ├── MistakeItemRepository.cs       # 错题条目仓储实现
│   │       └── ProcessLogRepository.cs        # 操作日志仓储实现
│   ├── Service/
│   │   └── MistakeServiceImpl.cs              # gRPC 实现 (SubmitMistakeUpload)
│   └── Contract/Protos/
│       ├── mistake.proto                      # gRPC 契约：SubmitMistakeUpload 定义
│       └── mistake.common.proto                # gRPC 公共类型定义
├── test/
│   └── Ruoyu.Study.Mistake.Domain.Tests/
│       └── MistakeDomainServiceTests.cs       # 单元测试
└── docs/MistakeDispatch/SubmitMistakeUpload/  # 本文档所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

PostgreSQL 表 `mistake_item` 列：`id (uuid, PK)`, `student_id (varchar(36), NOT NULL)`, `subject (int)`, `grade (int)`, `source_upload_id (varchar(64), NOT NULL)`, `source_regions (jsonb)`, `review_status (int)`, `created_at (timestamptz)`, `updated_at (timestamptz, ConcurrencyCheck)`。

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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    // ...其他审核相关字段
}

public class SourceRegionModel
{
    public string SourceImagePath { get; set; } = string.Empty;
    public BoundingBoxModel? BoundingBox { get; set; }
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<List<MistakeItemModel>> SubmitMistakeUploadAsync(
    string studentId, int subject, int grade, List<string> imagePaths, string? comments, string sourceUploadId)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc SubmitMistakeUpload(SubmitMistakeUploadRequest) returns (SubmitMistakeUploadResponse);

message SubmitMistakeUploadRequest {
  string student_id = 1;
  int32 subject = 2;
  int32 grade = 3;
  repeated string image_paths = 4;
  string comments = 5;
  string source_upload_id = 6;
}

message SubmitMistakeUploadResponse {
  bool success = 1;
  string error_message = 2;
  repeated string created_item_ids = 3;
}
```

### 仓储接口

```csharp
// src/Domain/Repositories/IMistakeItemRepository.cs
public interface IMistakeItemRepository
{
    Task AddAsync(MistakeItemModel model);
    Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId);
    // ... 其他方法
}

// src/Domain/Repositories/IProcessLogRepository.cs
public interface IProcessLogRepository
{
    Task AddAsync(ProcessLogModel model);
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

### 错题上传提交流程 (`SubmitMistakeUpload`)

1. gRPC 层接收 `SubmitMistakeUploadRequest`，校验 student_id 非空、subject 在 1-9 范围、grade 在 1-12 范围、source_upload_id 非空、image_paths 非空。
2. 调用 `MistakeDomainService.SubmitMistakeUploadAsync(studentId, subject, grade, imagePaths, comments, sourceUploadId)`。
3. 领域服务调用 `ValidateSubmitData` 方法，收集所有校验错误，若有错误则抛出 `MistakeValidationException`，消息中包含所有错误（以 `; ` 分隔）。
4. 领域服务构建 `MistakeItemModel`：分配新 `Guid.Id`、将 `imagePaths` 转换为 `List<SourceRegionModel>`（每个区域的 `BoundingBox` 为 null）、设置 `ReviewStatus = (int)ReviewStatus.PendingReview`、`CreatedAt = DateTimeOffset.UtcNow`。
5. 调用 `_mistakeItemRepository.AddAsync(item)` 写入错题条目。
6. 调用 `AddProcessLogAsync(item.Id, ProcessAction.Created, "Mistake item created from upload")` 写入操作日志。
7. 调用 `_unitOfWork.SaveChangesAsync()` 提交事务。
8. 返回包含新创建的 `MistakeItemModel` 的列表（当前仅返回一条）。
9. gRPC 层将 `Item.Id` 转换为字符串加入 `response.CreatedItemIds`，设置 `Success = true` 并返回。

## 错误处理策略

- **参数校验（双层）**：gRPC 层前置判空与范围检查；领域服务层 `ValidateSubmitData` 做二次聚合校验，收集所有错误信息统一抛出 `MistakeValidationException`。
- **异常转换**：gRPC 层捕获 `MistakeValidationException` 并转换为 `RpcException(StatusCode.InvalidArgument)`。
- **事务保护**：错题条目与操作日志的写入由 `IUnitOfWork.SaveChangesAsync` 一次性提交，避免部分写入。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 错题条目 Add/GetById/Update/Delete/List | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IProcessLogRepository` | 操作日志 Add/GetByMistakeItemId/RemoveByMistakeItemId | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.Mistake.Domain` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository`、`IProcessLogRepository`、`IUnitOfWork` 全部通过构造函数注入，可被 Moq 替换。
- **时间可替换**：当前代码直接使用 `DateTimeOffset.UtcNow`；测试不依赖精确时间戳，只做范围断言。
- **校验逻辑独立**：`ValidateSubmitData` 为静态方法，可独立测试各种参数组合。
- **失败路径可触发**：Moq `ThrowsAsync` 可模拟仓储/事务失败，验证回滚逻辑。