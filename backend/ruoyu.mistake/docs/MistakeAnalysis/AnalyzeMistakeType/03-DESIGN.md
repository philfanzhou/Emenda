# AnalyzeMistakeType — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── MistakeItemModel.cs              # 错题条目领域模型
│   │   │   └── ProcessLogModel.cs               # 处理日志领域模型
│   │   ├── Services/
│   │   │   └── MistakeDomainService.cs          # 领域服务 (AnalyzeMistakeTypeAsync)
│   │   └── Repositories/
│   │       ├── IMistakeItemRepository.cs        # 错题条目仓储接口
│   │       ├── IProcessLogRepository.cs         # 处理日志仓储接口
│   │       └── IUnitOfWork.cs                   # 事务工作单元接口
│   ├── Service/
│   │   └── MistakeServiceImpl.cs               # gRPC 实现 (AnalyzeMistakeType)
│   └── Contract/Protos/
│       └── mistake.proto                       # gRPC 契约定义
└── docs/MistakeAnalysis/AnalyzeMistakeType/     # 本文档所在目录
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
public async Task<MistakeTypeResponse> AnalyzeMistakeTypeAsync(Guid mistakeItemId)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc AnalyzeMistakeType(AnalyzeMistakeRequest) returns (MistakeTypeResponse);

message AnalyzeMistakeRequest {
  string mistake_item_id = 1;
  string content = 2;           // 当前未使用
  string correct_answer = 3;    // 当前未使用
  string student_answer = 4;    // 当前未使用
}

message MistakeTypeResponse {
  int32 type = 1;
  string type_name = 2;
  string analysis_reason = 3;
}
```

### gRPC 层实现

```csharp
// src/Service/MistakeServiceImpl.cs
public override async Task<MistakeTypeResponse> AnalyzeMistakeType(
    AnalyzeMistakeRequest request, ServerCallContext context)
{
    return await _mistakeService.AnalyzeMistakeTypeAsync(Guid.Parse(request.MistakeItemId));
}
```

### 领域服务注入依赖

```csharp
// 构造函数签名
public MistakeDomainService(
    IMistakeItemRepository mistakeItemRepository,
    IMistakePracticeRepository mistakePracticeRepository,
    IProcessLogRepository processLogRepository,
    IUnitOfWork unitOfWork,
    ILogger<MistakeDomainService> logger)
```

## 数据流描述（步骤序列）

### 分析错题类型流程 (`AnalyzeMistakeType`)

1. gRPC 层接收 `AnalyzeMistakeRequest`，将 `MistakeItemId` 字符串通过 `Guid.Parse` 解析为 GUID。
2. 调用 `MistakeDomainService.AnalyzeMistakeTypeAsync(mistakeItemId)`。
3. 领域服务构造硬编码的分析结果：`type = 5`、`typeName = "其他错误"`、`analysisReason = "根据答案对比无法明确分类"`。
4. 通过 `_mistakeItemRepository.GetByIdAsync` 查询错题条目是否存在。
5. 若条目存在，调用私有方法 `AddProcessLogAsync` 记录一条 `ProcessAction.Analyzed` 类型的日志（`details = "Mistake analyzed as 其他错误"`）。
6. 调用 `_unitOfWork.SaveChangesAsync` 提交事务。
7. 返回 `MistakeTypeResponse`。

## 错误处理策略

- **GUID 格式**：`Guid.Parse` 在传入非法格式时抛出 `FormatException`，由 gRPC 框架转为 Internal 错误返回给调用方。
- **错题不存在**：`GetByIdAsync` 返回 null 时，不记录日志、不提交事务，仅返回硬编码的分析结果。
- **事务失败**：`SaveChangesAsync` 异常向上抛出，由 gRPC 框架处理。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 通过 ID 查询错题条目 `GetByIdAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IProcessLogRepository` | 写入处理日志 `AddAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IUnitOfWork` | 事务提交 `SaveChangesAsync` | `Ruoyu.Study.Mistake.Domain` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository`、`IProcessLogRepository`、`IUnitOfWork` 全部通过构造函数注入，可被 Moq 替换。
- **分析结果确定**：当前为硬编码返回，测试可对 `Type == 5` 做精确断言。
- **日志写入可验证**：Mock `IProcessLogRepository.AddAsync` 可验证日志是否正确写入、内容是否正确。
- **错题不存在分支可测**：Mock `GetByIdAsync` 返回 null 可测试无日志记录分支。