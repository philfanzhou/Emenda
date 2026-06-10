# ReviewMistakeItem - 设计文档

## 文件结构

```
src/Contract/Protos/mistake.proto            # RPC 和 message 定义
src/Contract/Protos/mistake.common.proto     # 公共枚举（ReviewStatus, ProcessAction）
src/Service/MistakeServiceImpl.cs            # gRPC 服务实现（含图片迁移逻辑）
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
MistakeServiceImpl (gRPC 层后续处理)
    │
    │  if (Confirmed):
    │    3. 遍历 SourceRegions
    │       - 收集所有 SourceImagePath → removedPaths
    │       - 若以 "uploads/" 开头:
    │         a. OSS Copy: uploads/xxx → mistakes/xxx
    │         b. OSS Delete: uploads/xxx
    │         c. 更新 region.SourceImagePath = newPath
    │    4. 若有迁移 → UpdateMistakeItemAsync(item)
    │
    ▼
返回 ReviewMistakeItemResponse
  { success, source_upload_id, student_id, removed_image_paths }
```

## 关键设计决策

1. **审核与迁移分离**: 领域层只负责审核状态更新和 ProcessLog 记录；图片迁移由 gRPC 层负责，因为 OSS 操作属于基础设施关注点，不属于领域逻辑。

2. **removed_image_paths 语义**: 返回所有 SourceImagePath（不仅是迁移的），用于调用方从上传记录中移除这些图片引用。即使图片不以 `uploads/` 开头，也会被收集到 removed_image_paths。

3. **可选字段处理**: grade 和 subject 在 gRPC 层判断 `> 0` 才传入领域层，领域层判断 `> 0 && <= 范围上限` 才更新。两层协作确保无效值被忽略。

4. **图片迁移路径规则**: `uploads/xxx` → `mistakes/xxx`，仅替换前缀 "uploads" 为 "mistakes"，保留后续路径结构。

5. **审核幂等性**: 不支持重复审核。已审核（Confirmed 或 Rejected）的条目再次审核会抛出 MistakeValidationException。
