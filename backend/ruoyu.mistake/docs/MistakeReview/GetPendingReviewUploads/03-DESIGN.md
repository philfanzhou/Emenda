# GetPendingReviewUploads — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── MistakeItemModel.cs              # 领域模型（包含 PendingReviewUploadModel）
│   │   ├── Services/
│   │   │   └── MistakeDomainService.cs          # 领域服务 (GetPendingReviewUploadsAsync)
│   │   └── Repositories/
│   │       └── IMistakeItemRepository.cs        # 错题仓储接口 (GetPendingReviewUploadsAsync)
│   ├── Database/
│   │   ├── Entities/
│   │   │   └── MistakeItemEntity.cs             # EF Core 实体
│   │   └── Repositories/
│   │       └── MistakeItemRepository.cs         # 错题仓储实现（包含 SQL 聚合查询）
│   ├── Service/
│   │   └── MistakeServiceImpl.cs                # gRPC 实现 (GetPendingReviewUploads)
│   └── Contract/Protos/
│       ├── mistake.proto                        # gRPC 契约定义 (GetPendingReviewUploadsRequest/PendingReviewUploadListResponse)
│       └── mistake.common.proto                 # 公共类型 (PendingReviewUploadDto/PageMeta)
├── test/
│   └── (待创建)                                   # 单元测试
└── docs/MistakeReview/GetPendingReviewUploads/    # 本文档所在目录
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
// src/Domain/Models/MistakeItemModel.cs
public class PendingReviewUploadModel
{
    public string SourceUploadId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public int Subject { get; set; }              // 学科 1-9
    public int Grade { get; set; }                // 年级 1-12
    public int PendingCount { get; set; }         // 待审核错题数
    public int TotalCount { get; set; }           // 总错题数
    public DateTimeOffset EarliestCreatedAt { get; set; }  // 最早提交时间
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(
    int page,
    int size,
    List<int>? subjects = null)
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc GetPendingReviewUploads(GetPendingReviewUploadsRequest) returns (PendingReviewUploadListResponse);

message GetPendingReviewUploadsRequest {
  int32 page = 1;
  int32 size = 2;
  repeated int32 subjects = 3;   // 可选学科筛选
}

message PendingReviewUploadListResponse {
  repeated PendingReviewUploadDto uploads = 1;
  PageMeta page_meta = 2;
}

// 来自 mistake.common.proto
message PendingReviewUploadDto {
  string source_upload_id = 1;
  string student_id = 2;
  int32 subject = 3;
  int32 grade = 4;
  int32 pending_count = 5;
  int32 total_count = 6;
  string earliest_created_at = 7;
}
```

### 仓储接口

```csharp
// src/Domain/Repositories/IMistakeItemRepository.cs
Task<(List<PendingReviewUploadModel> Uploads, int TotalCount)> GetPendingReviewUploadsAsync(
    int page, int size, List<int>? subjects = null);
```

## 数据流描述（步骤序列）

### 待审核上传列表查询流程 (`GetPendingReviewUploads`)

1. gRPC 层接收 `GetPendingReviewUploadsRequest`。
2. 若 `request.Subjects` 非空，构建 `List<int> subjects` 传递；否则传 `null`。
3. 调用 `MistakeDomainService.GetPendingReviewUploadsAsync(page, size, subjects)`。
4. 领域服务执行分页参数修正：`page <= 0 → page = 1`；`size <= 0 → size = 10`；`size > 100 → size = 100`。
5. 调用 `_mistakeItemRepository.GetPendingReviewUploadsAsync(page, size, subjects)`。
6. 仓储层在数据库中执行聚合查询：
   - 按 `source_upload_id` 分组，筛选存在 `ReviewStatus == PendingReview` 记录的分组。
   - 统计每组的 `pending_count`（PendingReview 数量）、`total_count`（总数量）。
   - 若 `subjects` 非空，增加 `WHERE subject IN (...)` 过滤。
   - 计算 `earliest_created_at`（最早错题提交时间）。
   - 分页返回结果和总记录数。
7. 领域服务将结果返回 gRPC 层。
8. gRPC 层构建 `PendingReviewUploadListResponse`：
   - 设置 `PageMeta`（page/size/total_count/total_pages）。
   - 将 `PendingReviewUploadModel` 列表映射为 `PendingReviewUploadDto` 列表。
   - `earliest_created_at` 为 `DateTimeOffset.MinValue` 时输出空字符串。

## 错误处理策略

- **分页参数修正**：领域服务静默修正非法分页参数（page≤0、size≤0、size>100），不抛出异常。
- **空结果**：无待审核记录时正常返回空列表，total_count 为 0，不视为错误。
- **仓储异常**：由 EF Core / PostgreSQL 层抛出，向上传播至 gRPC 拦截器统一处理。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 待审核上传聚合查询 `GetPendingReviewUploadsAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository` 通过构造函数注入，可被 Moq 替换。
- **分页修正逻辑独立**：分页参数修正逻辑在领域服务中执行，不依赖数据库，可直接单元测试。
- **subject 过滤可验证**：Moq 可验证传入的 subjects 参数是否正确传递。
- **空结果可模拟**：Moq 返回空列表和 totalCount=0。