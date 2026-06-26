# SubmitMistakeUpload - 设计文档

## 文件结构

```
src/Contract/Protos/mistake.proto          # RPC 和 message 定义
src/Contract/Protos/mistake.common.proto   # 公共枚举和 DTO
src/Service/MistakeServiceImpl.cs          # gRPC 服务实现
src/Domain/Services/MistakeDomainService.cs # 领域服务
src/Domain/Models/MistakeItemModel.cs      # 领域模型
src/Domain/Models/ProcessLogModel.cs       # ProcessLog 模型
src/Domain/Repositories/IMistakeItemRepository.cs  # 仓储接口
src/Domain/Repositories/IProcessLogRepository.cs   # ProcessLog 仓储接口
src/Domain/Repositories/IUnitOfWork.cs     # 工作单元接口
src/Database/Entities/MistakeItemEntity.cs # 数据库实体
src/Database/Entities/ProcessLogEntity.cs  # ProcessLog 实体
src/Database/Repositories/MistakeItemRepository.cs # 仓储实现
```

## 关键接口签名

### gRPC RPC

```protobuf
rpc SubmitMistakeUpload(SubmitMistakeUploadRequest) returns (SubmitMistakeUploadResponse);
```

### Proto Messages

```protobuf
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

### 领域服务方法

```csharp
public async Task<List<MistakeItemModel>> SubmitMistakeUploadAsync(
    string studentId, int subject, int grade,
    List<string> imagePaths, string? comments, string sourceUploadId)
```

### 仓储接口

```csharp
// IMistakeItemRepository
Task AddAsync(MistakeItemModel model);

// IProcessLogRepository
Task AddAsync(ProcessLogModel model);

// IUnitOfWork
Task<int> SaveChangesAsync();
```

## 依赖的数据库表/字段

### mistake_item 表

| 字段 | 类型 | 说明 |
|------|------|------|
| id | GUID (PK) | 主键 |
| student_id | VARCHAR(36) | 学生ID |
| subject | INT | 学科 (1-9) |
| grade | INT | 年级 (1-12) |
| source_upload_id | VARCHAR(64) | 上传记录ID |
| source_regions | JSON | 图片区域列表（序列化为 owned collection） |
| review_status | INT | 审核状态 |
| created_at | DATETIMEOFFSET | 创建时间 |
| updated_at | DATETIMEOFFSET | 更新时间（ConcurrencyCheck） |

### process_log 表

| 字段 | 类型 | 说明 |
|------|------|------|
| id | GUID (PK) | 主键 |
| mistake_item_id | GUID (FK) | 关联错题条目 |
| action | INT | 操作类型 |
| details | TEXT | 操作详情 |
| created_at | DATETIMEOFFSET | 创建时间 |

## 数据流/调用链

```
gRPC Client
    │
    ▼
MistakeServiceImpl.SubmitMistakeUpload()
    │  1. 验证请求参数 (studentId, subject, grade, sourceUploadId, imagePaths)
    │  2. 验证失败 → RpcException(InvalidArgument)
    │
    ▼
MistakeDomainService.SubmitMistakeUploadAsync()
    │  1. ValidateSubmitData() - 领域层验证
    │  2. 创建 MistakeItemModel (ReviewStatus=PendingReview)
    │  3. 每个 imagePath → SourceRegionModel (BoundingBox=null)
    │
    ├──► IMistakeItemRepository.AddAsync(item)
    │
    ├──► AddProcessLogAsync(item.Id, Created, "Mistake item created from upload")
    │       └──► IProcessLogRepository.AddAsync(log)
    │
    └──► IUnitOfWork.SaveChangesAsync()
    │
    ▼
返回 List<MistakeItemModel> → 提取 ID 列表 → SubmitMistakeUploadResponse
```

## 关键设计决策

1. **双层验证**: gRPC 层和领域层均执行参数验证。gRPC 层先拦截明显无效请求，领域层确保业务规则一致性。领域层验证会将多个错误合并为一条消息（分号分隔）。

2. **单条目返回**: 虽然 `SubmitMistakeUploadAsync` 返回 `List<MistakeItemModel>`，但当前实现只创建一个 MistakeItemModel（包含所有图片路径作为 SourceRegions）。返回列表是为了未来扩展。

3. **BoundingBox 初始为 null**: 提交时仅记录图片路径，裁剪区域（BoundingBox）留待后续分析步骤填充。

4. **不检查重复**: 同一 `sourceUploadId` 可以多次提交，每次创建新条目。这允许学生对同一上传进行补充提交。

5. **UploadPollingWorker 幂等检查**: Worker 在处理每条记录前，通过 `IMistakeDomainService.GetMistakeItemsByUploadAsync(uploadId)` 查询该 uploadId 下已有的 MistakeItem，从 SourceRegions 中提取已处理图片路径集合。然后计算未处理图片 = 记录全部图片 - 已处理图片。只对未处理图片执行 VL 分析、MistakeItem 创建和 OSS 迁移。若全部图片已处理，则跳过 VL/OSS，直接通知 Student 删除全部图片。这确保 Worker 重复轮询同一条记录时不会产生重复数据，同时能自动清理因之前 bug 遗留的脏数据。

6. **VL 请求体构造约定（跨模块共享）**: `UploadPollingWorker` 与 `ReanalyzeJobHostedService` 都通过 `IVlAnalyzer` 间接调用 `VlUploadAnalyzer`。VL 请求体**必须**包含 `max_tokens = VlAnalyzerOptions.MaxOutputTokens`（默认 4096），目的是为当前可用的推理型模型（mimo-v2.5、step-3.7-flash）预留 reasoning 预算，避免 content 被截断。详细说明、根因诊断、复现数据见 `docs/overview/SystemContext.md` 中 "VL Provider" 段和 `docs/HumanReview.md` HR-12。
