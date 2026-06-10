# 错题类型分析 - 设计文档

## 文件结构

| 文件 | 项目相对路径 | 说明 |
|------|-------------|------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` | RPC: `AnalyzeMistakeType(AnalyzeMistakeRequest) returns (MistakeTypeResponse)` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` | `Empty` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` | `AnalyzeMistakeType` 方法 |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` | `AnalyzeMistakeTypeAsync(Guid)` |
| 仓储接口 | `src/Domain/Repositories/IMistakeItemRepository.cs` | `GetByIdAsync(Guid)` |
| 仓储实现 | `src/Database/Repositories/MistakeItemRepository.cs` | EF Core 查询 |
| 仓储接口 | `src/Domain/Repositories/IProcessLogRepository.cs` | `AddAsync(ProcessLogModel)` |
| 领域模型 | `src/Domain/Models/ProcessLogModel.cs` | ProcessLog 领域模型 |

## 关键接口签名

### Proto 定义

```protobuf
message AnalyzeMistakeRequest {
  string mistake_item_id = 1;
  string content = 2;           // ⚠️ 未使用
  string correct_answer = 3;    // ⚠️ 未使用
  string student_answer = 4;    // ⚠️ 未使用
}

message MistakeTypeResponse {
  int32 type = 1;
  string type_name = 2;
  string analysis_reason = 3;
}
```

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<MistakeTypeResponse> AnalyzeMistakeType(AnalyzeMistakeRequest request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<MistakeTypeResponse> AnalyzeMistakeTypeAsync(Guid mistakeItemId)
```

## 依赖的数据库表

| 表名 | 关键字段 | 说明 |
|------|---------|------|
| `mistake_items` | `id` | 用于查询错题是否存在 |
| `process_log` | `id`, `mistake_item_id`, `action`, `details`, `created_at` | 记录分析日志 |

## 数据流/调用链

```
Client
  │
  ▼
MistakeServiceImpl.AnalyzeMistakeType(AnalyzeMistakeRequest)
  │  Guid.Parse(request.MistakeItemId)
  │  ⚠️ content/correct_answer/student_answer 被忽略
  ▼
MistakeDomainService.AnalyzeMistakeTypeAsync(Guid mistakeItemId)
  │
  ├── 硬编码: type=5, typeName="其他错误", analysisReason="根据答案对比无法明确分类"
  │
  ├── IMistakeItemRepository.GetByIdAsync(mistakeItemId)
  │     │
  │     ├── item != null ──► AddProcessLogAsync(item.Id, ProcessAction.Analyzed, "Mistake analyzed as 其他错误")
  │     │                      └── IProcessLogRepository.AddAsync(...)
  │     │                      └── IUnitOfWork.SaveChangesAsync()
  │     │
  │     └── item == null ──► 不记录日志
  │
  ▼
MistakeTypeResponse { Type=5, TypeName="其他错误", AnalysisReason="根据答案对比无法明确分类" }
```

## 关键设计决策

1. **硬编码占位**: 当前分析逻辑为硬编码返回，原因是真正的错题类型分析算法尚未设计。此占位实现确保 API 可用，前端可先行集成。
2. **Proto 参数预留**: `content`/`correct_answer`/`student_answer` 在 Proto 中定义但未传入领域服务，为未来实现预留接口。
3. **条件性日志记录**: 仅当错题存在时才记录 ProcessLog，避免为不存在的错题产生无效日志。
4. **返回值不依赖查询结果**: 无论错题是否存在，都返回相同的硬编码 MistakeTypeResponse。
5. **领域服务返回 gRPC 类型**: `AnalyzeMistakeTypeAsync` 直接返回 `MistakeTypeResponse`（gRPC 生成类），存在领域层对契约层的耦合，未来重构时应考虑使用领域模型。
