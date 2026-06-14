# ReviewMistakeItem - 设计文档

## 文件结构

```
src/Contract/Protos/mistake.proto            # RPC 和 message 定义
src/Contract/Protos/mistake.common.proto     # 公共枚举（ReviewStatus, ProcessAction）
src/Service/MistakeServiceImpl.cs            # gRPC 服务实现（含图片迁移 + 后审核编排逻辑）
src/Service/StudentLearningGrpcServiceClient.cs  # 学生服务 gRPC 客户端（RemoveImagesFromRecord, MarkUploadRecordAsReturned）
src/Domain/Services/MistakeDomainService.cs  # 领域服务
src/Domain/Models/MistakeItemModel.cs        # 领域模型
src/Domain/Models/ProcessLogModel.cs         # ProcessLog 模型
src/Domain/Repositories/IMistakeItemRepository.cs  # 仓储接口
src/Domain/Repositories/IProcessLogRepository.cs   # ProcessLog 仓储接口
src/Domain/Repositories/IUnitOfWork.cs       # 工作单元接口
src/Database/Entities/MistakeItemEntity.cs   # 数据库实体
src/Database/Entities/ProcessLogEntity.cs    # ProcessLog 实体
src/Database/Repositories/MistakeItemRepository.cs # 仓储实现
```

## 关键接口签名

### gRPC RPC

```protobuf
rpc ReviewMistakeItem(ReviewMistakeItemRequest) returns (ReviewMistakeItemResponse);
```

### Proto Messages

```protobuf
message ReviewMistakeItemRequest {
  string id = 1;
  ReviewStatus review_status = 2;
  string reviewer_id = 3;
  string review_comment = 4;
  int32 grade = 5;
  int32 subject = 6;
}

message ReviewMistakeItemResponse {
  bool success = 1;
  string error_message = 2;
  string source_upload_id = 3;
  string student_id = 4;
  repeated string removed_image_paths = 5;
}
```

### 领域服务方法

```csharp
public async Task<MistakeItemModel?> ReviewMistakeItemAsync(
    Guid id, int reviewStatus, string? reviewerId,
    string? reviewComment, int? grade = null, int? subject = null)
```

### 仓储接口

```csharp
// IMistakeItemRepository
Task<MistakeItemModel?> GetByIdAsync(Guid id);
Task<bool> UpdateAsync(MistakeItemModel model);
Task<bool> DeleteAsync(Guid id);
Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId);

// IProcessLogRepository
Task AddAsync(ProcessLogModel model);

// IUnitOfWork
Task<int> SaveChangesAsync();
```

### 学生服务 gRPC 客户端

```csharp
// StudentLearningGrpcServiceClient
Task RemoveImagesFromRecord(string sourceUploadId, IEnumerable<string> imagePaths);
Task MarkUploadRecordAsReturned(string sourceUploadId);
Task<MigrateImagesToMistakeResponse> MigrateImagesToMistakeAsync(IEnumerable<string> sourcePaths);
```

## 依赖的数据库表/字段

### mistake_item 表

| 字段 | 类型 | 说明 |
|------|------|------|
| id | GUID (PK) | 主键 |
| review_status | INT | 审核状态 (1=PendingReview, 2=Confirmed, 3=Rejected) |
| reviewer_id | VARCHAR(36) | 审核人ID |
| reviewed_at | DATETIMEOFFSET | 审核时间 |
| review_comment | TEXT | 审核评论 |
| subject | INT | 学科（审核时可更新） |
| grade | INT | 年级（审核时可更新） |
| source_regions | JSON | 图片区域列表（含 SourceImagePath） |
| updated_at | DATETIMEOFFSET | 更新时间（ConcurrencyCheck） |

### process_log 表

| 字段 | 类型 | 说明 |
|------|------|------|
| id | GUID (PK) | 主键 |
| mistake_item_id | GUID (FK) | 关联错题条目 |
| action | INT | 操作类型 (3=ReviewConfirmed, 4=ReviewRejected) |
| details | TEXT | 操作详情 |
| created_at | DATETIMEOFFSET | 创建时间 |

## 数据流/调用链

```
gRPC Client
    │
    ▼
MistakeServiceImpl.ReviewMistakeItem()
    │  1. 验证 review_status 只能是 Confirmed 或 Rejected
    │  2. 解析可选的 grade/subject（>0 才传入）
    │
    ▼
MistakeDomainService.ReviewMistakeItemAsync()
    │  1. GetByIdAsync(id) → 获取错题条目
    │  2. 验证 ReviewStatus == PendingReview
    │  3. 验证 reviewStatus == Confirmed || Rejected
    │  4. 更新 ReviewStatus/ReviewerId/ReviewedAt/ReviewComment/UpdatedAt
    │  5. 可选更新 Grade/Subject
    │
    ├──► IMistakeItemRepository.UpdateAsync(item)
    │
    ├──► AddProcessLogAsync(item.Id, ReviewConfirmed/ReviewRejected, detail)
    │       └──► IProcessLogRepository.AddAsync(log)
    │
    └──► IUnitOfWork.SaveChangesAsync()
    │
    ▼ 返回 MistakeItemModel
MistakeServiceImpl (gRPC 层后审核编排)
    │
    │  if (Confirmed):
    │    3. 遍历 SourceRegions
    │       - 收集所有 SourceImagePath → removedPaths
    │       - 若以 "uploads/" 开头: 收集到 sourcePaths
    │    4. 若 sourcePaths 非空:
    │       - 调用 StudentLearningGrpcServiceClient.MigrateImagesToMistakeAsync(sourcePaths)
    │       - 遍历迁移结果: 成功 → 更新 region.SourceImagePath = newPath
    │    5. 若有迁移 → UpdateMistakeItemAsync(item)
    │    6. 调用 StudentLearningGrpcServiceClient.RemoveImagesFromRecord(sourceUploadId, removedPaths)
    │
    │  if (Rejected):
    │    3. 删除该 MistakeItem (IMistakeItemRepository.DeleteAsync)
    │
    │  (Confirmed 和 Rejected 共用):
    │    6. 查询该 sourceUploadId 下是否还有 PendingReview 状态的条目
    │       - 若无 → 调用 StudentLearningGrpcServiceClient.MarkUploadRecordAsReturned(sourceUploadId)
    │       - 若有 → 不调用
    │
    ▼
返回 ReviewMistakeItemResponse
  { success, source_upload_id, student_id, removed_image_paths }
```

## 关键设计决策

1. **审核与后审核编排分离**: 领域层只负责审核状态更新和 ProcessLog 记录；图片迁移、学生服务回调等后审核编排由 gRPC 层负责，因为 OSS 操作和跨服务调用属于基础设施关注点，不属于领域逻辑。

2. **编排从 Controller 下沉到 gRPC 层**: 原先由 teacher_portal Controller 负责的跨服务编排（调用 CompleteUploadReview、RemoveImagesFromRecord、MarkUploadRecordAsReturned）已下沉到 mistake 服务的 gRPC 层。好处：(a) 减少门户层的跨服务协调复杂度；(b) 审核后编排逻辑内聚在 mistake 服务，portal 只需调用 ReviewMistakeItem 即可；(c) 避免 portal 与 student 服务的直接耦合。

3. **removed_image_paths 语义**: 返回所有 SourceImagePath（不仅是迁移的），用于调用方从上传记录中移除这些图片引用。即使图片不以 `uploads/` 开头，也会被收集到 removed_image_paths。注意：现在 gRPC 层已直接调用 RemoveImagesFromRecord，removed_image_paths 仍保留在响应中供调用方参考，但 portal 不再需要据此自行调用 student 服务。

4. **可选字段处理**: grade 和 subject 在 gRPC 层判断 `> 0` 才传入领域层，领域层判断 `> 0 && <= 范围上限` 才更新。两层协作确保无效值被忽略。

5. **图片迁移路径规则**: `uploads/xxx` → `mistakes/xxx`，由 Student 服务 `MigrateImagesToMistake` 处理，Mistake 服务根据返回结果更新路径。

6. **审核幂等性**: 不支持重复审核。已审核（Confirmed 或 Rejected）的条目再次审核会抛出 MistakeValidationException。

7. **驳回删除 MistakeItem**: Rejected 时直接删除该 MistakeItem，而非仅标记状态。因为驳回的错题不应保留在系统中，学生需要重新上传。

8. **无待审核条目时自动标记上传记录**: 审核完成后检查该 sourceUploadId 下是否还有 PendingReview 条目，若无则调用 MarkUploadRecordAsReturned。这替代了原先 CompleteUploadReview 的功能，使审核流程在逐条审核时即时完成。
