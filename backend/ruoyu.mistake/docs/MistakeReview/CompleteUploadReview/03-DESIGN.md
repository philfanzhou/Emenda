# CompleteUploadReview — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── MistakeItemModel.cs              # 领域模型（SourceRegions 路径管理）
│   │   ├── Services/
│   │   │   └── MistakeDomainService.cs          # 领域服务 (CompleteUploadReviewAsync / CompleteUploadReviewResult)
│   │   └── Repositories/
│   │       ├── IMistakeItemRepository.cs        # 错题仓储 (GetBySourceUploadIdAsync/CountPendingBySourceUploadIdAsync)
│   │       └── IUnitOfWork.cs                   # 事务控制
│   ├── Service/
│   │   └── MistakeServiceImpl.cs                # gRPC 实现 (CompleteUploadReview — 包含 OSS 迁移和跨服务调用)
│   ├── Contract/Protos/
│   │   └── mistake.proto                        # gRPC 契约定义 (CompleteUploadReviewRequest/CompleteUploadReviewResponse)
│   └── (引用)
│       └── Ruoyu.Study.Common.Oss.IOssService    # OSS 操作接口 (CopyObjectAsync/DeleteAsync)
├── test/
│   └── (待创建)                                   # 单元测试
└── docs/MistakeReview/CompleteUploadReview/       # 本文档所在目录
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
// src/Domain/Services/MistakeDomainService.cs
public class CompleteUploadReviewResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SourceUploadId { get; set; }
    public string? StudentId { get; set; }
    public List<MistakeItemModel> Items { get; set; } = new();
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<CompleteUploadReviewResult> CompleteUploadReviewAsync(
    string sourceUploadId,
    string? reviewerId)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc CompleteUploadReview(CompleteUploadReviewRequest) returns (CompleteUploadReviewResponse);

message CompleteUploadReviewRequest {
  string source_upload_id = 1;
  string reviewer_id = 2;
}

message CompleteUploadReviewResponse {
  bool success = 1;
  string error_message = 2;
  string student_id = 3;
}
```

### OSS 服务接口（来自 Common 模块）

```csharp
// Ruoyu.Study.Common.Oss
public interface IOssService
{
    Task CopyObjectAsync(string sourcePath, string targetPath);
    Task DeleteAsync(string path);
}
```

### Student 服务 gRPC 接口

```protobuf
// Ruoyu.Study.Student.Contract.Protos
service StudentLearningGrpcService {
    rpc RemoveImagesFromRecord(RemoveImagesFromRecordRequest) returns (RemoveImagesFromRecordResponse);
}

message RemoveImagesFromRecordRequest {
    string record_id = 1;
    string student_id = 2;
    repeated string image_paths = 3;
}
```

### 注入的外部依赖

```csharp
public MistakeServiceImpl(
    MistakeDomainService mistakeService,
    IOssService ossService,
    StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient studentClient,
    ILogger<MistakeServiceImpl> logger)
```

- `MistakeDomainService`：领域服务，提供 `CompleteUploadReviewAsync`（校验收敛）
- `IOssService`：OSS 操作，提供 `CopyObjectAsync` / `DeleteAsync`
- `StudentLearningGrpcServiceClient`：Student 服务 gRPC 客户端，提供 `RemoveImagesFromRecordAsync`

## 数据流描述（步骤序列）

### 完成上传审核流程 (`CompleteUploadReview`)

1. gRPC 层接收 `CompleteUploadReviewRequest`。
2. 调用 `MistakeDomainService.CompleteUploadReviewAsync(sourceUploadId, reviewerId)`。
3. 领域服务校验：
   - 调用 `_mistakeItemRepository.CountPendingBySourceUploadIdAsync(sourceUploadId)` 统计待审核数量。
   - `pendingCount > 0`：返回 `Success = false`，`ErrorMessage = "还有 N 条错题待审核，请先完成所有审核"`。
   - 调用 `_mistakeItemRepository.GetBySourceUploadIdAsync(sourceUploadId)` 获取所有错题。
   - `items.Count == 0`：返回 `Success = false`，`ErrorMessage = "未找到该上传记录的错题"`。
   - 校验通过：返回 `Success = true`，`Items` 为所有错题列表，`StudentId` 取自第一条错题。
4. gRPC 层收到结果；若 `!result.Success`，直接返回 `CompleteUploadReviewResponse { Success = false, ErrorMessage = result.ErrorMessage }`。
5. gRPC 层初始化：
   - `pathMapping`（`Dictionary<string, string>`）：路径映射缓存，避免重复 OSS 操作。
   - `imagePathsToRemoveFromRecord`（`List<string>`）：收集需要从上传记录中移除的图片路径。
6. 遍历 `result.Items` 中的每条错题：
   - 对每条 `SourceRegion`：
     - 将 `SourceImagePath` 加入 `imagePathsToRemoveFromRecord` 列表（去重）。
     - 若路径不以 `uploads/` 开头，跳过（continue）。
     - 若错题 `ReviewStatus != Confirmed`（已驳回），记录日志 "Item {ItemId} is not Confirmed...not migrate"，跳过（continue）。
     - 若路径已在 `pathMapping` 缓存中，直接使用缓存的新路径更新 `SourceImagePath`，标记 `updated = true`。
     - 否则：计算新路径 `newPath = "mistakes" + oldPath.Substring("uploads".Length)`。
     - 调用 `_ossService.CopyObjectAsync(oldPath, newPath)`。
     - 调用 `_ossService.DeleteAsync(oldPath)`。
     - 加入缓存：`pathMapping[oldPath] = newPath`。
     - 更新 `SourceImagePath = newPath`，标记 `updated = true`。
     - 若 OSS 操作抛出异常：`_logger.LogError` 记录错误，继续处理后续图片（不中断）。
   - 若 `updated`，调用 `_mistakeService.UpdateMistakeItemAsync(item)` 持久化路径更新。
7. 若 `imagePathsToRemoveFromRecord` 非空且 `studentId` 非空：
   - 构建 `RemoveImagesFromRecordRequest { RecordId = sourceUploadId, StudentId = studentId, ImagePaths = [...] }`。
   - 调用 `_studentClient.RemoveImagesFromRecordAsync(removeImagesRequest)`。
   - 若调用失败：`_logger.LogError` 记录错误，不影响返回结果。
8. 返回 `CompleteUploadReviewResponse { Success = true, StudentId = result.StudentId }`。

## 错误处理策略

- **前置校验**：领域服务在事务外执行校验，`pendingCount > 0` 或 `items.Count == 0` 时返回 `Success = false`，不进入 OSS 操作。
- **OSS 操作容错**：单个图片的 `CopyObjectAsync` / `DeleteAsync` 失败时 catch 异常并记录 Error 日志，不中断主循环。
- **路径缓存**：`pathMapping` 字典确保同一路径只迁移一次，PartialFailure 场景下已成功路径不会重复操作。
- **跨服务调用容错**：Student 服务 `RemoveImagesFromRecord` 调用失败时仅记录日志，不影响整体 `Success` 返回。
- **领域异常**：`MistakeValidationException` 由 gRPC 层 catch 并转换为 `CompleteUploadReviewResponse { Success = false }`。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | `CountPendingBySourceUploadIdAsync` / `GetBySourceUploadIdAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IOssService` | 对象拷贝 `CopyObjectAsync` / 对象删除 `DeleteAsync` | `Ruoyu.Study.Common.Oss` |
| `StudentLearningGrpcServiceClient` | 移除上传记录图片 `RemoveImagesFromRecordAsync` | `Ruoyu.Study.Student.Contract.Protos` |
| `MistakeDomainService.UpdateMistakeItemAsync` | 更新错题路径 | `Ruoyu.Study.Mistake.Domain.Services` |

## 可测试性设计

- **依赖注入接口化**：`IOssService`、`StudentLearningGrpcServiceClient` 全部通过构造函数注入，可被 Moq 替换。
- **校验逻辑与 OSS 操作分离**：领域服务只做校验和数据返回，OSS 操作在 gRPC 层执行，可分别单元测试。
- **路径映射可验证**：Moq `IOssService` 可追踪调用次数和参数，验证路径缓存逻辑。
- **失败路径可触发**：Moq `ThrowsAsync` 可模拟 OSS 拷贝失败、Student 服务调用失败。