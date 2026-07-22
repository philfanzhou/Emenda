# GetPendingReviewUploads - 设计文档

## 文件结构

```
src/Contract/Protos/mistake.proto            # RPC 和 message 定义
src/Contract/Protos/mistake.common.proto     # 公共 DTO（PageMeta, PendingReviewUploadDto）
src/Service/MistakeServiceImpl.cs            # gRPC 服务实现
src/Domain/Services/MistakeDomainService.cs  # 领域服务
src/Domain/Models/MistakeItemModel.cs        # 领域模型（含 PendingReviewUploadModel）
src/Domain/Repositories/IMistakeItemRepository.cs  # 仓储接口
src/Database/Repositories/MistakeItemRepository.cs # 仓储实现（含客户端分组聚合）
src/Database/Entities/MistakeItemEntity.cs   # 数据库实体
```

## 关键接口签名

### gRPC RPC

```protobuf
rpc GetPendingReviewUploads(GetPendingReviewUploadsRequest) returns (PendingReviewUploadListResponse);
```

### Proto Messages

```protobuf
message GetPendingReviewUploadsRequest {
  int32 page = 1;
  int32 size = 2;
  repeated int32 subjects = 3;
}

message PendingReviewUploadListResponse {
  repeated PendingReviewUploadDto uploads = 1;
  PageMeta page_meta = 2;
}

message PendingReviewUploadDto {
  string source_upload_id = 1;
  string student_id = 2;
  int32 subject = 3;
  int32 grade = 4;
  int32 pending_count = 5;
  int32 total_count = 6;
  string earliest_created_at = 7;
}

message PageMeta {
  int32 page = 1;
  int32 size = 2;
  int32 total_count = 3;
  int32 total_pages = 4;
}
```

### 领域服务方法

```csharp
public async Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(
    int page, int size, List<int>? subjects = null)
```

### 仓储接口

```csharp
// IMistakeItemRepository
Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(
    int page, int size, List<int>? subjects = null);
```

### 领域模型

```csharp
public class PendingReviewUploadModel
{
    public string SourceUploadId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }
    public int Grade { get; set; }
    public int PendingCount { get; set; }
    public int TotalCount { get; set; }
    public DateTimeOffset EarliestCreatedAt { get; set; }
}
```

## 依赖的数据库表/字段

### mistake_item 表（查询用）

| 字段 | 类型 | 说明 |
|------|------|------|
| source_upload_id | VARCHAR(64) | 分组键 |
| student_id | VARCHAR(36) | 分组键 |
| subject | INT | 分组键 + 筛选条件 |
| grade | INT | 分组键 |
| review_status | INT | 筛选条件 (PendingReview=1) |
| created_at | DATETIMEOFFSET | 聚合 Min + 排序 |

## 数据流/调用链

```
gRPC Client
    │
    ▼
MistakeServiceImpl.GetPendingReviewUploads()
    │  1. 解析 subjects（空列表 → null）
    │
    ▼
MistakeDomainService.GetPendingReviewUploadsAsync()
    │  1. 分页修正: page<=0→1, size<=0→10, size>100→100
    │
    ▼
MistakeItemRepository.GetPendingReviewUploadsAsync()
    │  1. 查询 ReviewStatus=PendingReview 的条目
    │  2. 可选 subjects 筛选
    │  3. ToListAsync() 获取所有数据（客户端聚合）
    │  4. 按 {SourceUploadId, StudentId, Subject, Grade} 分组
    │  5. 计算每组 PendingCount 和 EarliestCreatedAt
    │  6. 按 EarliestCreatedAt 升序排列
    │  7. 客户端分页: Skip + Take
    │  8. 映射为 PendingReviewUploadModel
    │
    ▼
MistakeServiceImpl (gRPC 层映射)
    │  1. 构建 PageMeta (page, size, total_count, total_pages)
    │  2. 映射 PendingReviewUploadModel → PendingReviewUploadDto
    │     - EarliestCreatedAt: MinValue → 空字符串, 否则 ISO 8601 UTC
    │
    ▼
返回 PendingReviewUploadListResponse
```

## 关键设计决策

1. **客户端分组聚合**: 将所有 PendingReview 条目加载到内存后进行 LINQ 分组聚合。在数据量可控（size 上限 100）的场景下是可接受的权衡。

2. **分组键包含四个字段**: 按 `{SourceUploadId, StudentId, Subject, Grade}` 分组，意味着同一上传批次如果包含不同学科或年级的错题，会被拆分为多行。这确保了每个分组的信息一致性。

3. **PendingCount == TotalCount**: 当前实现仅查询 PendingReview 状态的条目，因此 PendingCount 和 TotalCount 值相同。TotalCount 字段为未来扩展预留（如包含已审核条目的统计）。

4. **分页在聚合后执行**: 先分组聚合得到总数，再对分组结果进行 Skip/Take 分页，确保分页基于分组后的行数而非原始条目数。

5. **subjects 空列表处理**: gRPC 层将空的 subjects 列表转为 null 传入领域层，避免空列表被误用为筛选条件。
