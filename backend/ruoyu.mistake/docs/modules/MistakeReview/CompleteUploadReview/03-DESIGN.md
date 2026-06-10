# 完成上传审核 — 设计说明 (DESIGN)

## 文件结构

```
src/
├── Contract/Protos/mistake.proto              # gRPC 定义: CompleteUploadReview RPC
├── Contract/Protos/mistake.common.proto       # 枚举定义: ReviewStatus
├── Service/MistakeServiceImpl.cs              # gRPC 实现: CompleteUploadReview handler + 图片迁移
├── Domain/Services/MistakeDomainService.cs    # 领域服务: CompleteUploadReviewAsync + CompleteUploadReviewResult
├── Domain/Repositories/IMistakeItemRepository.cs  # 仓储接口: CountPendingBySourceUploadIdAsync, GetBySourceUploadIdAsync
└── Database/Repositories/MistakeItemRepository.cs  # 仓储实现
```

## 关键接口签名

### gRPC (proto)

```protobuf
rpc CompleteUploadReview(CompleteUploadReviewRequest) returns (CompleteUploadReviewResponse);

message CompleteUploadReviewRequest {
    string source_upload_id = 1;
    string reviewer_id = 2;
}

message CompleteUploadReviewResponse {
    bool success = 1;
    string error_message = 2;
    string student_id = 3;
    repeated string removed_image_paths = 4;
}
```

### Domain Service

```csharp
public class CompleteUploadReviewResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SourceUploadId { get; set; }
    public string? StudentId { get; set; }
    public List<MistakeItemModel> Items { get; set; } = new();
}

public async Task<CompleteUploadReviewResult> CompleteUploadReviewAsync(
    string sourceUploadId, string? reviewerId)
```

### Repository

```csharp
Task<int> CountPendingBySourceUploadIdAsync(string sourceUploadId);
Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId);
```

## 数据流

```
Client → gRPC:
  CompleteUploadReviewRequest { sourceUploadId, reviewerId }

MistakeServiceImpl.CompleteUploadReview:
  1. 调用 _mistakeService.CompleteUploadReviewAsync(sourceUploadId, reviewerId)
  2. 若 result.Success == false → 返回 { Success=false, ErrorMessage }
  3. 遍历 result.Items:
     a. 遍历 item.SourceRegions:
        - 收集所有图片路径到 imagePathsToRemoveFromRecord
        - 若路径不以 "uploads/" 开头 → 跳过迁移
        - 若 item 非 Confirmed → 仅移除记录，不迁移
        - 若 pathMapping 已缓存 → 使用缓存路径
        - 否则执行 OSS Copy + Delete，更新 region.SourceImagePath
     b. 若有更新 → 调用 _mistakeService.UpdateMistakeItemAsync(item)
  4. 返回 CompleteUploadReviewResponse { Success=true, StudentId, RemovedImagePaths }

MistakeDomainService.CompleteUploadReviewAsync:
  1. 调用 _mistakeItemRepository.CountPendingBySourceUploadIdAsync(sourceUploadId)
  2. 若 pendingCount > 0 → 返回 { Success=false, ErrorMessage="还有 N 条错题待审核..." }
  3. 调用 _mistakeItemRepository.GetBySourceUploadIdAsync(sourceUploadId)
  4. 若 items.Count == 0 → 返回 { Success=false, ErrorMessage="未找到该上传记录的错题" }
  5. 返回 { Success=true, SourceUploadId, StudentId=items[0].StudentId, Items }
```

## 依赖的数据库

| 表 | 字段 | 用途 |
|----|------|------|
| [mistake_item](../../database/tables/mistake_item.md) | source_upload_id, review_status | 查询待审核数量 |
| [mistake_item](../../database/tables/mistake_item.md) | source_upload_id | 查询该上传记录下所有错题 |
| [mistake_item](../../database/tables/mistake_item.md) | source_regions | 读取/更新图片路径 |
| [mistake_item](../../database/tables/mistake_item.md) | student_id | 返回学生ID |

## 关键设计决策

- **Domain Service 不依赖 OSS**：图片迁移逻辑放在 gRPC Service 层，Domain Service 只负责业务校验和数据查询，保持领域层纯净
- **pathMapping 缓存**：同一图片可能被多条错题引用，使用 `Dictionary<string, string>` 缓存已迁移路径，避免重复 OSS 操作
- **OSS 失败容错**：单个图片迁移失败仅记录 Error 日志，不中断整体流程，不影响其他图片的迁移
- **removedImagePaths 语义**：包含所有错题的图片路径（无论是否迁移），供调用方从上传记录中移除
