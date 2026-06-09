# GetMistakePractices — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── MistakePracticeModel.cs         # 错题练习领域模型
│   ├── Services/
│   │   └── MistakeDomainService.cs             # 领域服务 (GetPracticesAsync)
│   ├── Repositories/
│   │   └── IMistakePracticeRepository.cs       # 错题练习仓储接口
│   ├── Database/
│   │   └── Repositories/
│   │       └── MistakePracticeRepository.cs    # 错题练习仓储实现
│   ├── Service/
│   │   └── MistakeServiceImpl.cs               # gRPC 实现 (GetMistakePractices)
│   └── Contract/Protos/
│       ├── mistake.proto                       # gRPC 契约：GetMistakePractices 定义
│       └── mistake.common.proto                # gRPC 公共类型：MistakePracticeDto, IdRequest
├── test/
│   └── Ruoyu.Study.Mistake.Domain.Tests/
│       └── MistakeDomainServiceTests.cs        # 单元测试
└── docs/MistakePractice/GetMistakePractices/   # 本文档所在目录
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
public async Task<List<MistakePracticeModel>> GetPracticesAsync(Guid mistakeItemId)
```

- 简单委托方法，直接调用 `_mistakePracticeRepository.GetByMistakeItemIdAsync(mistakeItemId)` 返回结果。
- 不做任何业务校验或数据加工。

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc GetMistakePractices(IdRequest) returns (MistakePracticeListResponse);

// mistake.common.proto
message IdRequest {
  string id = 1;
}

message MistakePracticeListResponse {
  repeated MistakePracticeDto practices = 1;
}

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
    Task<List<MistakePracticeModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
    Task AddAsync(MistakePracticeModel model);
    Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);
    // ... 其他方法
}
```

### 注入的外部依赖

```csharp
public MistakeDomainService(
    IMistakeItemRepository mistakeItemRepository,
    IMistakePracticeRepository mistakePracticeRepository,  // <-- 本功能使用
    IProcessLogRepository processLogRepository,
    IUnitOfWork unitOfWork,
    ILogger<MistakeDomainService> logger)
```

## 数据流描述（步骤序列）

### 获取错题练习流程 (`GetMistakePractices`)

1. gRPC 层接收 `IdRequest`，将 `request.Id` 解析为 `Guid.Parse(request.Id)`。
2. 调用 `MistakeDomainService.GetPracticesAsync(Guid)`。
3. 领域服务直接调用 `_mistakePracticeRepository.GetByMistakeItemIdAsync(mistakeItemId)`。
4. 仓储实现层通过 EF Core 查询 `mistake_practice` 表中 `mistake_item_id == mistakeItemId` 的所有记录。
5. 返回 `List<MistakePracticeModel>`（可能为空列表）。
6. gRPC 层通过 `MapPracticeDto` 将每条 `MistakePracticeModel` 转换为 `MistakePracticeDto`。
7. 将转换后的 DTO 列表赋值到 `MistakePracticeListResponse.Practices` 并返回。

### `MapPracticeDto` 转换逻辑

```csharp
private static MistakePracticeDto MapPracticeDto(MistakePracticeModel practice)
{
    var dto = new MistakePracticeDto
    {
        Id = practice.Id.ToString(),
        MistakeItemId = practice.MistakeItemId.ToString(),
        StudentId = practice.StudentId ?? string.Empty,
        PracticeDate = practice.PracticeDate == DateTimeOffset.MinValue
            ? string.Empty
            : practice.PracticeDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        StudentAnswer = practice.StudentAnswer ?? string.Empty,
        IsCorrect = practice.IsCorrect,
        Comments = practice.Comments ?? string.Empty
    };
    dto.ImagePaths.AddRange(practice.ImagePaths ?? new List<string>());
    return dto;
}
```

## 错误处理策略

- **查询操作**：纯只读操作，不抛业务异常。若没有匹配记录，返回空列表。
- **参数解析**：`Guid.Parse` 对非法字符串格式抛出 `FormatException`，由 gRPC 运行时自动处理为 `RpcException`。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakePracticeRepository` | 练习记录 GetByMistakeItemIdAsync 查询 | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`IMistakePracticeRepository` 通过构造函数注入，可被 Moq 替换。
- **纯委托方法**：`GetPracticesAsync` 仅转发调用，测试重点在于验证仓储被正确调用且返回结果与 mock 一致。
- **无副作用**：查询操作不调用 `SaveChangesAsync`，不需要事务 mock。