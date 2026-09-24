# ReanalyzeMistakeItem — 设计说明 (DESIGN)

> **迁出说明（2026-09-24）**：本文中的 `src/admin_portal` / `admin_portal` 路径已随 Admin Portal 迁出为外部仓库 [Ruoyu.Admin](https://github.com/philfanzhou/Ruoyu.Admin)（[ADR-0010](../../../../../../../docs/adr/0010-extract-ruoyu-admin.md)），在本仓库已不存在。本文保留原始描述作为历史记录，不作为当前运行手册使用。


## 本功能在项目中的目录与文件结构

```
src/services/ruoyu.mistake/
├── Contract/Protos/
│   ├── mistake.common.proto          # 现有 PROCESS_ACTION_REANALYZED=7
│   └── mistake.proto                 # 改造 ReanalyzeMistakeItem* 为 StartReanalyzeJob / GetReanalyzeJobs / GetActiveReanalyzeJobs
├── Domain/
│   ├── Models/
│   │   ├── ReanalyzeJobModel.cs      # 新增：Job 领域模型
│   │   ├── MistakeItemModel.cs       # 不变
│   │   └── ProcessLogModel.cs        # 不变
│   ├── Repositories/
│   │   └── IReanalyzeJobRepository.cs# 新增
│   ├── Services/
│   │   ├── IReanalyzeJobService.cs   # 新增：领域编排（启动/状态/崩溃恢复/清理）
│   │   ├── ReanalyzeJobService.cs    # 新增
│   │   ├── IMistakeDomainService.cs  # 保留 Reanalyze/PersistReanalyzed 方法签名（实现下沉到 IReanalyzeJobService 内部）
│   │   └── MistakeDomainService.cs   # 保留（其它方法不变）
│   └── Errors/
│       └── ReanalyzeJobError.cs      # 新增：领域错误码
├── Database/
│   ├── Entities/
│   │   └── ReanalyzeJobEntity.cs     # 新增：EF Core 实体
│   ├── MistakeDbContext.cs           # 改造：注册 ReanalyzeJobs DbSet
│   └── Migrations/
│       └── YYYYMMDDHHMMSS_AddReanalyzeJob.cs  # 新增：迁移
├── Host/
│   ├── Program.cs                    # 改造：注册 ReanalyzeJobHostedService + ReanalyzeJobCleanupService
│   └── Services/
│       ├── ReanalyzeJobHostedService.cs   # 新增：消费 Job 队列
│       └── ReanalyzeJobCleanupService.cs  # 新增：30 天清理
├── Service/
│   └── MistakeServiceImpl.cs         # 改造：旧 ReanalyzeMistakeItem 改名为 StartReanalyzeJob（语义：仅入队，不调 VL），新增 GetReanalyzeJobs / GetActiveReanalyzeJobs
└── Tests/
    ├── Ruoyu.Study.Mistake.Service.Tests/
    │   ├── ReanalyzeJobServiceTests.cs       # 新增
    │   ├── ReanalyzeJobHostedServiceTests.cs # 新增
    │   ├── ReanalyzeJobCleanupServiceTests.cs# 新增
    │   ├── MistakeServiceImplRpcTests.cs     # 改造：旧 ReanalyzeMistakeItemRpcTests → StartReanalyzeJobRpcTests
    │   └── ReanalyzeMistakeItemTests.cs      # 移除或重写
    └── Ruoyu.Study.Mistake.Domain.Tests/
        └── ReanalyzeJobServiceTests.cs       # 新增

src/teacher_portal/backend/TeacherWebApi/
├── Controllers/MistakeController.cs  # 改造：3 个端点（start / batch-status / active-jobs）
├── Models/AuthModels.cs              # 改造：新增 StartReanalyzeJobResponse / ReanalyzeJobStatusDto
└── Tests/MistakeControllerTests.cs   # 改造：旧 Reanalyze 测试 → StartReanalyzeJob + GetReanalyzeJobs + GetActiveReanalyzeJobs

src/teacher_portal/frontend/src/
├── composables/
│   └── useReanalyzePolling.ts        # 新增：批量轮询 composable（5s 间隔）
├── components/
│   ├── ReviewDetail.vue              # 改造：提交即关闭弹窗 + 启动轮询
│   ├── RecordDetail.vue              # 改造：同上
│   ├── RecordCard.vue                # 改造：新增"AI 重新分析中"角标
│   └── RecordList.vue                # 改造：页面加载时拉取 active-jobs + 调度轮询
└── types/index.ts                    # 改造：ReanalyzeMistakeItemRequest → StartReanalyzeJobResponse / ReanalyzeJobStatus

src/admin_portal/                      # **不实现**（关键约束）
```

## 关键接口签名和数据结构定义

### gRPC 接口

```protobuf
// mistake.proto 新增 / 改造

// 旧 ReanalyzeMistakeItemRequest 保留字段但语义变为"仅入队"
message ReanalyzeMistakeItemRequest {
  string id = 1;                       // mistake_item.id
  string reviewer_id = 2;              // 教师 ID（BFF 从 JWT 提取）
  string teacher_description = 3;      // 可选
}

// 旧 ReanalyzeMistakeItemResponse 改造：成功时不再含 item，仅返回 jobId
message ReanalyzeMistakeItemResponse {
  bool success = 1;
  string error_message = 2;
  string job_id = 3;                   // 成功时返回
  // item 字段已移除：因 Job 异步执行，结果通过 jobs 查询接口获取
}

// 批量状态查询（新增）
message GetReanalyzeJobsRequest {
  repeated string job_ids = 1;         // 1-100 个
}

message GetReanalyzeJobsResponse {
  bool success = 1;
  string error_message = 2;
  repeated ReanalyzeJobDto jobs = 3;
}

// 教师当前活跃 Job（新增）
message GetActiveReanalyzeJobsRequest {}

message GetActiveReanalyzeJobsResponse {
  bool success = 1;
  string error_message = 2;
  repeated ReanalyzeJobDto jobs = 3;
}

// 单个 Job DTO
message ReanalyzeJobDto {
  string job_id = 1;
  string item_id = 2;
  string student_id = 3;
  ReanalyzeJobStatus status = 4;
  string new_root_cause = 5;
  string error_message = 6;
  string created_at = 7;
  string started_at = 8;
  string completed_at = 9;
  int64 vl_duration_ms = 10;
}

enum ReanalyzeJobStatus {
  REANALYZE_JOB_STATUS_UNSPECIFIED = 0;
  REANALYZE_JOB_STATUS_PENDING = 1;
  REANALYZE_JOB_STATUS_RUNNING = 2;
  REANALYZE_JOB_STATUS_COMPLETED = 3;
  REANALYZE_JOB_STATUS_FAILED = 4;
}
```

> proto 文件的 `StartReanalyzeJob` 名称改不改由实现期决定（沿用旧名 `ReanalyzeMistakeItem` 也可，但响应字段从 `item` 改为 `job_id`）。

### BFF 端点契约

| 方法 | 路径 | 请求 | 响应 | 鉴权 |
|------|------|------|------|------|
| POST | `/api/mistake/{id}/reanalyze?studentId=xxx` | `{"teacherDescription":"..."}` | 202 `{success, data:{jobId}}` / 4xx `{success:false,message}` | JWT |
| GET | `/api/mistake/reanalyze/jobs?ids=a,b,c` | — | 200 `{success, data:[{jobId,itemId,status,newRootCause,errorMessage,...}]}` | JWT |
| GET | `/api/mistake/reanalyze/active-jobs` | — | 200 `{success, data:[...]}` | JWT |

### C# 领域接口

```csharp
// IReanalyzeJobService.cs
public interface IReanalyzeJobService
{
    /// 入队：仅做校验和写库，不调 VL
    Task<StartReanalyzeJobResult> StartJobAsync(
        Guid itemId, string reviewerId, string studentId, string? teacherDescription, CancellationToken ct);

    /// 批量查询（鉴权：仅返回 reviewerId 拥有的）
    Task<List<ReanalyzeJobModel>> GetJobsAsync(
        string reviewerId, IReadOnlyList<Guid> jobIds, CancellationToken ct);

    /// 教师当前所有活跃 Job
    Task<List<ReanalyzeJobModel>> GetActiveJobsAsync(string reviewerId, CancellationToken ct);

    /// Worker 入口：从 DB 拉取所有 Pending Job
    Task<List<ReanalyzeJobModel>> ClaimPendingJobsAsync(int maxCount, CancellationToken ct);

    /// Worker 状态机：Pending → Running（写 started_at）
    Task<bool> MarkRunningAsync(Guid jobId, CancellationToken ct);

    /// Worker 状态机：Running → Completed（写 result + completed_at + 持久化 mistake_item.root_cause + ProcessLog）
    Task<bool> MarkCompletedAsync(Guid jobId, string newRootCause, long vlDurationMs, CancellationToken ct);

    /// Worker 状态机：Running → Failed（写 error + completed_at + ProcessLog，不动 root_cause）
    Task<bool> MarkFailedAsync(Guid jobId, string errorMessage, CancellationToken ct);

    /// 启动时清理：把 Running 标 Failed（崩溃恢复）
    Task<int> RecoverStaleRunningJobsAsync(CancellationToken ct);

    /// 清理 30 天前的 Completed/Failed
    Task<int> DeleteExpiredJobsAsync(DateTimeOffset cutoff, CancellationToken ct);
}

public class StartReanalyzeJobResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public ReanalyzeJobModel? Job { get; set; }
    public string? ErrorCode { get; set; }  // "NotFound" / "FailedPrecondition" / "InvalidArgument"
}
```

### Job 状态机

```
                ┌─────────┐
   Submit  ──▶  │ Pending │ ◀── Submit（同 itemId 入队）
                └────┬────┘
                     │ Worker claim
                     ▼
                ┌─────────┐
                │ Running │ ──── VL 成功 ──▶ Completed
                └────┬────┘ ──── VL 失败 ──▶ Failed
                     │
            ┌────────┴────────┐
            │ 服务重启         │ 服务长时间无响应（保留策略待定）
            ▼                 ▼
   MarkFailed("服务重启导致任务中断")
```

## 依赖的数据库表/字段/索引

### 新增表 `reanalyze_job`

| 列 | 类型 | 约束 | 说明 |
|----|------|------|------|
| `id` | uuid | PK | JobId（Guid） |
| `item_id` | uuid | NOT NULL, FK → mistake_item.id | 关联错题 |
| `reviewer_id` | varchar(36) | NOT NULL | 教师 ID |
| `student_id` | varchar(36) | NOT NULL | 学生 ID（冗余） |
| `teacher_description` | text | NULL | 教师补充描述 |
| `previous_root_cause` | text | NULL | 提交时的旧根因（快照） |
| `status` | int | NOT NULL | 0=Unspecified, 1=Pending, 2=Running, 3=Completed, 4=Failed |
| `error_message` | text | NULL | 失败时填 |
| `new_root_cause` | text | NULL | 成功时填 |
| `created_at` | timestamptz | NOT NULL | 入队时间 |
| `started_at` | timestamptz | NULL | 开始处理时间 |
| `completed_at` | timestamptz | NULL | 完成时间 |
| `vl_duration_ms` | bigint | NULL | VL 耗时 |

### 索引

```sql
CREATE INDEX ix_reanalyze_job_status_created_at ON reanalyze_job (status, created_at);  -- Worker 拉取 Pending
CREATE INDEX ix_reanalyze_job_reviewer_status  ON reanalyze_job (reviewer_id, status);   -- 教师活跃 Job 查询
CREATE INDEX ix_reanalyze_job_completed_at      ON reanalyze_job (completed_at) WHERE status IN (3, 4);  -- 清理
```

> 索引字段顺序按使用频率从高到低。

## 数据流/调用链

### 1. 提交 Reanalyze Job（同步路径，不调 VL）

```
[ReviewDetail.vue / RecordDetail.vue]
  ↓ POST /api/mistake/{id}/reanalyze?studentId=xxx  body={teacherDescription?: string}
[TeacherWebApi.MistakeController.StartReanalyzeJob]
  ├─ 提取 reviewer_id (JWT)
  ├─ 校验 studentId
  ├─ gRPC ReanalyzeMistakeItem
[MistakeServiceImpl.ReanalyzeMistakeItem]   ← 改造后
  ├─ 1. 校验 id 是 Guid
  ├─ 2. 查 MistakeItem（按 id + reviewerId 必填）
  ├─ 3. 校验 review_status ∈ {PendingReview, Confirmed}（否则 InvalidArgument/FailedPrecondition）
  ├─ 4. 校验 source_regions 非空
  ├─ 5. 调 IReanalyzeJobService.StartJobAsync
  │     └─ 创建 ReanalyzeJobModel{status=Pending, created_at=now, previous_root_cause=item.root_cause}
  │     └─ 写 DB
  └─ 返回 { success=true, job_id=<guid> }
  ↓
[前端 202] → 立即关闭弹窗，把 jobId 加入轮询集合
```

### 2. Worker 处理 Job（异步路径）

```
[ReanalyzeJobHostedService.ExecuteAsync loop]
  ├─ 启动时：RecoverStaleRunningJobsAsync（Running → Failed）
  ├─ 循环：
  │   ├─ ClaimPendingJobsAsync(batchSize=10)
  │   ├─ 写入 in-memory Channel（无界）
  │   └─ N 个 worker（MaxConcurrency=2）从 Channel 读 Job 并处理：
  │       ├─ MarkRunningAsync（写 started_at）
  │       ├─ 调 IVlAnalyzer.ReAnalyzeAsync(item.studentId, imagePaths, previous_root_cause, teacher_description)
  │       │     └─ VL 请求体构造约定：OpenAI 兼容 chat/completions，
  │       │        不发送 `max_tokens`，让 provider 使用自身默认输出限制。
  │       │        详见 docs/overview/SystemContext.md 中 "VL Provider" 段。
  │       ├─ 成功：MarkCompletedAsync
  │       │     └─ IReanalyzeJobService 内部：更新 mistake_item.root_cause / updated_at + 写 ProcessLog(Reanalyzed, details 含 job_id)
  │       └─ 失败：MarkFailedAsync
  │             └─ IReanalyzeJobService 内部：写 ProcessLog(Reanalyzed, details 含 job_id + "FAILED" 前缀)
  └─ 任意步骤异常：兜底 MarkFailedAsync（errorMessage = "内部异常: ..."）
```

### 3. 批量状态查询

```
[前端 useReanalyzePolling composable]
  ├─ 维护 Set<Guid> activeJobIds
  ├─ setInterval(5000ms)：
  │   └─ GET /api/mistake/reanalyze/jobs?ids=...
  │   └─ 遍历响应：
  │       ├─ status ∈ {Completed, Failed} → 从 Set 移除 + 刷新本地数据
  │       └─ status ∈ {Pending, Running} → 保留
  └─ 卸载时 clearInterval

[TeacherWebApi.MistakeController.GetReanalyzeJobs]
  ├─ 提取 reviewer_id (JWT)
  ├─ 解析 ids（split ','，去空，限制 1-100）
  ├─ gRPC GetReanalyzeJobs
[MistakeServiceImpl.GetReanalyzeJobs]
  └─ IReanalyzeJobService.GetJobsAsync(reviewerId, jobIds)  ← WHERE job_id IN (...) AND reviewer_id = ?
      └─ 返回 List<ReanalyzeJobModel>
```

### 4. 页面刷新恢复

```
[RecordList.vue onMounted]
  ├─ loadRecords()
  └─ 拉取 GET /api/mistake/reanalyze/active-jobs
      └─ 把响应里的 jobId 加入 Set，启动轮询
      └─ 卡片上显示"AI 重新分析中"角标
```

### 5. 30 天清理

```
[ReanalyzeJobCleanupService.ExecuteAsync loop]
  ├─ 启动后立即跑一次
  ├─ 之后每 24h 跑一次（生产）；开发态每 1h
  └─ IReanalyzeJobService.DeleteExpiredJobsAsync(now - 30d)
      └─ DELETE FROM reanalyze_job
         WHERE status IN (3, 4) AND completed_at < @cutoff
      └─ 返回删除行数，写 INFO 日志
```

## 原子性保证

`MarkCompletedAsync` / `MarkFailedAsync` 涉及 3 张表的写入（`mistake_item`、`reanalyze_job`、`process_log`）。**这 3 次写入必须由 1 次 SaveChanges 提交**，否则崩溃时会出现：

- item 已更新但 job 还停留在 `Running`（leak，直到 30 天后清理）
- job 已 Failed 但 item 还指向旧 root_cause（数据不一致）
- item 已更新但 process_log 没写（审计断裂）

实现策略：
- `IMistakeItemRepository.SetRootCauseDeferredAsync(itemId, rootCause, updatedAt)` —— **不** SaveChanges，仅 set 字段
- `IReanalyzeJobRepository.UpdateStatusAsync(model)` —— **不** SaveChanges
- `IProcessLogRepository.AddAsync(model)` —— 本来就不 SaveChanges
- `IUnitOfWork.SaveChangesAsync()` —— 统一一次性提交

只有 `MarkRunningAsync` 和 `StartJobAsync` 是单实体写入，它们自己显式 `SaveChanges`（也是 1 次提交，不混合其他写入）。

## 关键设计决策和取舍理由

1. **不取消**（用户决策）：教师发起 reanalysis 后必须等其跑完。简化了状态机、避免了协作式取消的复杂度。
2. **并发上限写死 2**：业务方明确要求不可配置——避免被错误配置改成 1 或 0 导致吞吐量意外变化。常量直接写在 `ReanalyzeJobHostedService` 静态字段。
3. **VL 失败不更新 root_cause**：沿用旧版（AC-FR-08）。Job 状态用 `Failed` + `error_message` 暴露给前端，前端可读出"分析失败，请重试"。
4. **Job 状态变更不修改 review_status**：与旧版一致，reanalysis ≠ 重新审核。
5. **同题多次提交直接入队**：业务方明确要求"排队"语义。同 itemId 的多个 Job 串行执行，第二个 Job 的 `previous_root_cause` 是第一个 Job 完成后的新值。
6. **30 天清理**：用单独 Hosted Service 跑批；不依赖 DB 调度（避免引入额外基础设施）。
7. **跨教师隔离**：所有查询 SQL 都带 `WHERE reviewer_id = @currentReviewerId`，即使 ID 命中也不返回别人数据。详见 AC-FR-03。
8. **批量查询而非长轮询/SSE**：业务方决策"前端轮询 5s"。一次查询多个 jobId，避免 N 个 Job 触发 N 次请求。
9. **复用现有 IVlAnalyzer + ProcessAction=7**：不引入新 AI 抽象；不引入新枚举值。
10. **Job 启动时 RecoverStaleRunningJobs**：处理"进程崩溃"边界——所有 Running 状态 Job 在启动时被标 Failed，避免悬挂。

## 与现有功能的边界

| 边界点 | ReanalyzeMistakeJob（新版） | ReviewMistakeItem | Student.ManualAnalyze |
|--------|------------------------------|-------------------|----------------------|
| gRPC 服务 | Mistake | Mistake | Student |
| 触发者 | 教师 | 教师 | 管理员 |
| 触发对象 | MistakeItem | MistakeItem | UploadRecord |
| 同步/异步 | **异步**（Job） | 同步 | 同步 |
| 输入 | 教师描述（可选） | 审核结果 | 无 |
| 输出 | JobId | 更新后 item | VL 原始结果 |
| 状态变更 | **不变** | Confirmed/Rejected | **不变** |
| Student 回调 | **无** | 删除图片引用 | **无** |
| 失败行为 | root_cause 保留 + Job=Failed | 返回错误 | 返回错误 |
| 并发控制 | 全局 2 个并发槽 | 不适用 | 不适用 |
| 历史保留 | 30 天 | 永久 | 不适用 |
