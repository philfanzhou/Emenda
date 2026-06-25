# ReanalyzeMistakeItem — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
src/services/ruoyu.mistake/
├── Contract/Protos/
│   ├── mistake.common.proto     # ProcessAction enum 新增 REANALYZED=7
│   └── mistake.proto            # 新增 ReanalyzeMistakeItemRequest/Response + RPC
├── Domain/
│   ├── Services/
│   │   ├── IMistakeDomainService.cs    # 新增 ReanalyzeMistakeItemAsync
│   │   └── MistakeDomainService.cs     # 实现
│   └── Models/
│       └── MistakeItemModel.cs         # RootCause 字段已存在，无需新增
├── Host/Services/VlImageAnalysis/
│   ├── IVlAnalyzer.cs            # 新增 ReAnalyzeAsync 接口
│   └── VlUploadAnalyzer.cs       # 新增 ReanalyzePrompt + ReAnalyzeAsync
├── Service/
│   └── MistakeServiceImpl.cs     # 新增 ReanalyzeMistakeItem gRPC override
└── Tests/Ruoyu.Study.Mistake.Service.Tests/
    └── ReanalyzeMistakeItemTests.cs   # 新增测试套件

src/teacher_portal/backend/TeacherWebApi/
├── Controllers/MistakeController.cs    # 新增 POST /api/mistake/{id}/reanalyze
├── Models/AuthModels.cs                # 新增请求/响应模型
└── Tests/MistakeControllerTests.cs     # 新增 BFF 单元测试

src/teacher_portal/frontend/src/
├── components/
│   ├── ReviewDetail.vue       # 审核时"AI 重新分析"按钮
│   └── RecordDetail.vue       # 已确认详情时"AI 重新分析"按钮
└── types/index.ts             # ReanalyzeMistakeItemRequest 等

src/admin_portal/                           # **不实现此功能**（关键约束）
```

## 关键接口签名和数据结构定义

```protobuf
// mistake.common.proto
enum ProcessAction {
  PROCESS_ACTION_UNSPECIFIED = 0;
  PROCESS_ACTION_CREATED = 1;
  PROCESS_ACTION_ANALYZED = 2;
  PROCESS_ACTION_REVIEW_CONFIRMED = 3;
  PROCESS_ACTION_REVIEW_REJECTED = 4;
  PROCESS_ACTION_PRACTICED = 5;
  PROCESS_ACTION_MASTERED = 6;
  PROCESS_ACTION_REANALYZED = 7;  // 新增
}

// mistake.proto
message ReanalyzeMistakeItemRequest {
  string id = 1;
  string reviewer_id = 2;            // 教师 ID（从 JWT 提取）
  string teacher_description = 3;   // 可选：教师补充描述
}

message ReanalyzeMistakeItemResponse {
  bool success = 1;
  string error_message = 2;
  MistakeItemDto item = 3;          // 成功时返回更新后的 item
}

service MistakeGrpcService {
  // ... 现有 RPC
  rpc ReanalyzeMistakeItem(ReanalyzeMistakeItemRequest) returns (ReanalyzeMistakeItemResponse);
}
```

```csharp
// IVlAnalyzer.cs
public interface IVlAnalyzer
{
  Task<VlAnalysisResult> AnalyzeAsync(string studentId, List<string> imagePaths, CancellationToken ct = default);

  // 新增
  Task<VlReanalysisResult> ReAnalyzeAsync(
      string studentId,
      List<string> imagePaths,
      string existingRootCause,
      string? teacherDescription,
      CancellationToken ct = default);
}

public class VlReanalysisResult
{
  public string RootCause { get; set; } = string.Empty;
  public string? RawResponse { get; set; }
  public bool Skipped { get; set; }
  public bool Success => !string.IsNullOrEmpty(RootCause) || Skipped;
}
```

```csharp
// IMistakeDomainService.cs 新增
Task<MistakeReanalysisResult> ReanalyzeMistakeItemAsync(
    Guid id, string? reviewerId, string? teacherDescription, CancellationToken ct = default);

public class MistakeReanalysisResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public MistakeItemModel? Item { get; set; }
}
```

## 依赖的数据库表/字段/索引

无新增表/字段。复用的字段：

| 表 | 字段 | 用途 |
|----|------|------|
| `mistake_item` | `id`, `root_cause`, `source_regions`, `review_status`, `updated_at` | 读/写 |
| `process_log` | `mistake_item_id`, `action`, `details`, `created_at`, `student_id` | 写 |

## 数据流/调用链

### Teacher Portal 审核时（PendingReview 状态）

```
[ReviewDetail.vue]
  ↓ POST /api/mistake/{id}/reanalyze  body={teacherDescription?: string}
[TeacherWebApi.MistakeController.ReanalyzeMistake]
  ↓ 提取 reviewer_id (JWT) → 校验 studentId
  ↓ gRPC ReanalyzeMistakeItem
[MistakeServiceImpl.ReanalyzeMistakeItem]
  ↓ 校验 ID 格式
[MistakeDomainService.ReanalyzeMistakeItemAsync]
  ├─ 1. 查 MistakeItem（按 id）
  ├─ 2. 校验 review_status ∈ {PendingReview, Confirmed}
  ├─ 3. 校验 source_regions 非空
  ├─ 4. 提取 imagePaths = source_regions.Select(r => r.SourceImagePath)
  ├─ 5. 调用 IVlAnalyzer.ReAnalyzeAsync(studentId, imagePaths, root_cause, teacher_description)
  │     ↓
  │   [VlUploadAnalyzer.ReAnalyzeAsync]
  │     ↓ HTTP POST Qwen3-VL
  │     ↓ prompt: 原 root_cause + 教师描述 + 图片
  │     ↓ 解析响应 → VlReanalysisResult
  ├─ 6. 若 VL 成功 → 更新 item.RootCause = newRootCause, item.UpdatedAt = now
  ├─ 7. 写 ProcessLog (Action=Reanalyzed, details=JSON)
  ├─ 8. SaveChangesAsync
  ↓
[返回更新后的 MistakeItemDto]
  ↓
[前端刷新 detail.value.rootCause, toast 提示成功]
```

### Teacher Portal 详情页（Confirmed 状态）

```
[RecordDetail.vue（错题本）]
  ↓ "AI 重新分析" 按钮 → 弹出 modal
  ↓ 提交 → POST /api/mistake/{id}/reanalyze
  ↓ （同上流程）
```

### VL Prompt 设计

```text
[Task Description]
You are re-analyzing a previously-analyzed mistake. The original analysis was:
"{existingRootCause}"

{If teacher_description provided:}
The teacher has provided additional context:
"{teacherDescription}"

Look at the {N} images and produce a refined one-sentence analysis of the error root cause in Chinese (no more than 50 characters). Focus on WHY the student made the mistake, not just WHAT the mistake is.

[Output Requirements]
Return ONLY a JSON object, no other text:
{ "rootCause": "..." }
```

## 关键设计决策和取舍理由

1. **不修改 ReviewStatus**：教师重新分析 ≠ 重新审核。保留原状态避免引入额外审核轮次，学生端的"已确认错题"不会因教师优化根因而重置。

2. **不持久化 teacher_description**：教师描述仅作为 VL 输入，**审计**通过 ProcessLog 保留（避免污染业务表 + 避免长文本膨胀行大小）。

3. **不写 process_log 字段额外列**：复用 `details TEXT` 字段存 JSON，避免新增表。

4. **不调用 Student 服务**：图片路径不变（已迁移到 `mistakes/`），无需移除引用或通知退回。

5. **VL 失败回滚原值**：保留原 root_cause 不变，仅写 ProcessLog 含失败原因。这保证业务连续性——AI 故障不导致错题被"掏空"。

6. **不暴露给 admin_portal**：根据用户要求，鉴权边界**只**在 teacher_portal BFF 实现。Mistake 服务本身不鉴权角色，由 BFF 端点契约保证。

7. **复用 IVlAnalyzer 接口**：不引入新 analyzer 类型，通过 `ReAnalyzeAsync` 方法区分初次/重新分析（共用 `IHttpClientFactory` + 配置）。

8. **不做频率限制**：当前用户量下风险可控；如未来需限制，可在 BFF 层加 rate limit（不污染 Mistake 服务）。

## 与现有功能的边界

| 边界点 | 本功能 | ReviewMistakeItem | Student.ManualAnalyze |
|--------|--------|-------------------|----------------------|
| gRPC 服务 | Mistake | Mistake | Student |
| 触发者 | 教师 | 教师 | 管理员 |
| 触发对象 | MistakeItem | MistakeItem | UploadRecord |
| 输入 | 教师描述（可选）| 审核结果 | 无 |
| 输出 | 新 root_cause | 更新后 item | VL 原始结果 |
| 状态变更 | **不变** | Confirmed/Rejected | **不变** |
| Student 回调 | **无** | 删除图片引用 | **无** |
