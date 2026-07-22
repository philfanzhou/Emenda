# ReanalyzeMistakeItem — 测试策略 (TESTS)

测试工具：xUnit + Moq + FluentAssertions

新增 / 改造的测试文件：

| 文件 | 职责 |
|------|------|
| `Tests/Ruoyu.Study.Mistake.Domain.Tests/ReanalyzeJobServiceTests.cs` | Domain 层：StartJob / 状态机 / 跨教师隔离 / Recover / DeleteExpired |
| `Tests/Ruoyu.Study.Mistake.Service.Tests/ReanalyzeJobHostedServiceTests.cs` | Hosted Service：并发=2、启动崩溃恢复、Channel 行为 |
| `Tests/Ruoyu.Study.Mistake.Service.Tests/ReanalyzeJobCleanupServiceTests.cs` | Cleanup Service：24h 周期 + 删除过期 |
| `Tests/Ruoyu.Study.Mistake.Service.Tests/MistakeServiceImplRpcTests.cs` | gRPC 层：StartReanalyzeJob 错误码 + GetReanalyzeJobs / GetActiveReanalyzeJobs |
| `Tests/Ruoyu.Study.Mistake.Service.Tests/StartReanalyzeJobTests.cs` | gRPC 旧 ReanalyzeMistakeItem 的 happy path 沿用（重命名为新流程） |
| `teacher_portal/backend/TeacherWebApi.Tests/MistakeControllerTests.cs` | BFF：3 个端点（Start / GetJobs / GetActiveJobs） |

> 旧 `ReanalyzeMistakeItemTests.cs`（同步版本）整体移除或重写为 `StartReanalyzeJobTests.cs`，断言从"item 内容"改为"jobId 返回"。

## Domain 层单元测试 — Given-When-Then 格式

### StartJobAsync 校验路径

#### UT-01 StartJobAsync_WithNonExistentItem_ReturnsNotFound

- **Given** `itemId` 在 DB 中不存在
- **When** `StartJobAsync(itemId, "t1", "s1", null, ct)`
- **Then** `Result.Success = false`，`ErrorCode = "NotFound"`，`ErrorMessage = "错题不存在"`
- **Then** 不创建 Job

#### UT-02 StartJobAsync_WithRejectedItem_ReturnsFailedPrecondition

- **Given** 错题存在，`review_status = Rejected`
- **When** `StartJobAsync(itemId, "t1", "s1", null, ct)`
- **Then** `Result.Success = false`，`ErrorCode = "FailedPrecondition"`，`ErrorMessage` 含"PendingReview 或 Confirmed"
- **Then** 不创建 Job

#### UT-03 StartJobAsync_WithEmptySourceRegions_ReturnsFailedPrecondition

- **Given** 错题存在，`source_regions = []`
- **When** `StartJobAsync`
- **Then** `Result.Success = false`，`ErrorCode = "FailedPrecondition"`，`ErrorMessage = "错题无图片，无法重新分析"`
- **Then** 不创建 Job

#### UT-04 StartJobAsync_WithValidItem_CreatesPendingJob

- **Given** 错题存在，`review_status = PendingReview`，`root_cause = "旧根因"`，`source_regions` 含 1 张
- **When** `StartJobAsync(itemId, "t1", "s1", "学生没读懂题意", ct)`
- **Then** `Result.Success = true`
- **Then** DB 写入 1 条 Job：`status = Pending`、`reviewer_id = "t1"`、`student_id = "s1"`、`teacher_description = "学生没读懂题意"`、`previous_root_cause = "旧根因"`
- **Then** `Result.Job` 字段全部填充正确
- **Then** `created_at` 接近 `DateTimeOffset.UtcNow`

#### UT-05 StartJobAsync_WithEmptyTeacherDescription_CreatesJobWithNullDescription

- **Given** 错题存在
- **When** `StartJobAsync(itemId, "t1", "s1", null, ct)`
- **Then** 成功；Job 的 `teacher_description = null`（或空字符串，与实现保持一致）

#### UT-06 StartJobAsync_SnapshotsRootCause_BeforeMutation

- **Given** 错题存在，`root_cause = "v1"`
- **When** 第一次 `StartJobAsync`（Job A 创建后**未**执行 VL）就再次 `StartJobAsync`（Job B）
- **Then** Job B 的 `previous_root_cause` = "v1"（**不是**"v1' "，因为 A 还没运行）
- **说明**：previous_root_cause 在 StartJob 时**立即**快照，不等 Job 执行

### 状态机测试

#### UT-10 MarkRunningAsync_TransitionsPendingToRunning_SetsStartedAt

- **Given** Job 存在，`Status = Pending`
- **When** `MarkRunningAsync(jobId, ct)`
- **Then** Job 变为 `Running`，`started_at` 接近 now
- **Then** 返回 `true`

#### UT-11 MarkRunningAsync_WithNonPendingJob_ReturnsFalse

- **Given** Job 存在，`Status = Completed`
- **When** `MarkRunningAsync(jobId, ct)`
- **Then** 返回 `false`（不修改）

#### UT-12 MarkCompletedAsync_TransitionsRunningToCompleted_WritesRootCauseAndLog

- **Given** Job 存在，`Status = Running`，`previous_root_cause = "old"`，`teacher_description = "note"`
- **And** `IVlAnalyzer` 已 mock 返回 `"new cause"`
- **When** `MarkCompletedAsync(jobId, "new cause", 1234L, ct)`
- **Then** Job 变为 `Completed`，`new_root_cause = "new cause"`，`completed_at ≈ now`，`vl_duration_ms = 1234`
- **Then** `mistake_item.root_cause` 更新为 "new cause"，`updated_at` 更新
- **Then** `process_log` 写入一条 `Action = 7 (Reanalyzed)`，`details` JSON 含 `job_id = <jobId>`
- **Then** `mistake_item.review_status` 不变

#### UT-13 MarkCompletedAsync_DoesNotChangeReviewStatus

- **Given** 错题 `review_status = Confirmed`
- **When** `MarkCompletedAsync`
- **Then** 完成后 `review_status` 仍为 `Confirmed`

#### UT-14 MarkFailedAsync_TransitionsRunningToFailed_DoesNotChangeRootCause

- **Given** 错题 `root_cause = "old"`，Job `Status = Running`
- **When** `MarkFailedAsync(jobId, "VL API 500", ct)`
- **Then** Job 变为 `Failed`，`error_message = "VL API 500"`，`completed_at ≈ now`
- **Then** `mistake_item.root_cause` 仍为 "old"（**未**被改）
- **Then** `process_log` 写入一条 `Action = 7`，`details` JSON 含 `job_id` + `"FAILED: VL API 500"`

#### UT-15 MarkFailedAsync_WithNonRunningJob_ReturnsFalse

- **Given** Job `Status = Pending`
- **When** `MarkFailedAsync(jobId, "reason", ct)`
- **Then** 返回 `false`（不修改）—— Worker 崩溃兜底不应用于 Pending 状态

### 跨教师隔离

#### UT-20 GetJobsAsync_FiltersByReviewerId

- **Given** Job A (`reviewer_id = "t1"`) 和 Job B (`reviewer_id = "t2"`)
- **When** `GetJobsAsync("t1", [jobA.Id, jobB.Id], ct)`
- **Then** 返回**只**包含 Job A（Job B 被过滤）

#### UT-21 GetActiveJobsAsync_ReturnsOnlyPendingAndRunning

- **Given** 教师 t1 有 3 个 Job：Pending / Running / Completed
- **When** `GetActiveJobsAsync("t1", ct)`
- **Then** 返回 Pending + Running 两个；Completed 不返回

#### UT-22 GetActiveJobsAsync_DoesNotReturnOtherReviewersJobs

- **Given** 教师 t1 有 Job A Pending；教师 t2 有 Job B Pending
- **When** `GetActiveJobsAsync("t1", ct)`
- **Then** 仅返回 Job A

### 崩溃恢复

#### UT-25 RecoverStaleRunningJobsAsync_MarksRunningAsFailed

- **Given** DB 中有 2 个 Job 处于 `Running` 状态（来自上次进程崩溃）
- **When** `RecoverStaleRunningJobsAsync(ct)`
- **Then** 2 个 Job 全部变为 `Failed`，`error_message = "服务重启导致任务中断"`，`completed_at` 填当前时间
- **Then** 返回 `2`
- **Then** 关联的 `mistake_item.root_cause` **未**被修改

#### UT-26 RecoverStaleRunningJobsAsync_LeavesPendingUntouched

- **Given** DB 中有 1 个 `Pending` + 1 个 `Running`
- **When** `RecoverStaleRunningJobsAsync`
- **Then** 仅 `Running` 那个被标 Failed；`Pending` 不变

### 清理

#### UT-30 DeleteExpiredJobsAsync_RemovesCompletedOlderThanCutoff

- **Given** 3 个 Completed Job：`completed_at` 分别为 now-29d / now-30d / now-31d
- **And** 1 个 Failed Job `completed_at = now-31d`
- **And** 1 个 Running Job（不应被清理）
- **When** `DeleteExpiredJobsAsync(now - 30d, ct)`
- **Then** 删除 2 个：now-31d Completed + now-31d Failed
- **Then** 返回 `2`
- **Then** 30d 边界的 Completed 保留（测试 off-by-one 风险）

#### UT-31 DeleteExpiredJobsAsync_LeavesPendingAndRunningUntouched

- **Given** 1 个 Pending（`completed_at = null`）+ 1 个 Running（`completed_at = null`）
- **And** 1 个 Failed `completed_at = now-100d`
- **When** `DeleteExpiredJobsAsync(now - 30d, ct)`
- **Then** 仅 Failed 被删；Pending/Running 不动

#### UT-32 MarkRunningAsync_TransitionsPendingToRunning_CommitsImmediately

- **Given** 1 个 Pending Job
- **When** `MarkRunningAsync(jobId)`
- **Then** `UpdateStatusAsync` 被调用一次（status: Running, started_at 填）
- **Then** `IUnitOfWork.SaveChangesAsync` **也被调用一次**（让后续 claim 周期立即看到 Running 状态，避免重复入队）

#### UT-33 MarkCompletedAsync_CommitsAllThreeTablesInOneSaveChanges

- **Given** 1 个 Running Job + 1 个 MistakeItem
- **When** `MarkCompletedAsync(jobId, "new", 1000)`
- **Then** 1 次 `SetRootCauseDeferredAsync` + 1 次 `UpdateStatusAsync` + 1 次 `AddAsync(process_log)` + 1 次 `IUnitOfWork.SaveChangesAsync`（**全部原子化**）
- **Then** mock UoW.SaveChanges 至少被调一次

#### UT-34 MarkFailedAsync_Atomicity_DoesNotChangeRootCause

- **Given** 1 个 Running Job
- **When** `MarkFailedAsync(jobId, "VL 500")`
- **Then** `mistake_item.RootCause` **保持原值**（`SetRootCauseDeferredAsync` **不**被调用）
- **Then** 1 次 `UpdateStatusAsync`（status: Failed）+ 1 次 `AddAsync(process_log)` + 1 次 SaveChanges

## Hosted Service 单元测试

### UT-40 HostedService_StartAsync_RecoversStaleRunningJobs_BeforeClaimingPending

- **Given** Mock `IReanalyzeJobService.RecoverStaleRunningJobsAsync` → 返回 2
- **And** Mock `ClaimPendingJobsAsync` → 返回 [jobA, jobB]
- **When** 启动 Hosted Service
- **Then** `Recover` 在 `Claim` **之前**被调用（用 `MockSequence` 验证顺序）

### UT-41 HostedService_ClaimPendingJobs_IsCalledPeriodically

- **Given** 用 `Mock<TimeProvider>` 控制时间
- **When** 虚拟时间推进到 30s
- **Then** `ClaimPendingJobsAsync` 被调用 ≥ 2 次（取决于具体实现周期；测试只需 ≥ 1）

### UT-42 HostedService_ProcessesUpToTwoJobsConcurrently

- **Given** `ClaimPendingJobsAsync` 返回 5 个 Job
- **And** 每个 Job 处理 Mock 模拟耗时 100ms（用 TaskCompletionSource 控制）
- **When** 启动 Hosted Service，等待 150ms
- **Then** **同时**有 2 个 Job 在 `MarkRunningAsync`（用并发计数 Moq Callback 验证）
- **Then** 300ms 后所有 5 个 Job 全部 Completed

### UT-43 HostedService_WhenVLFails_MarksJobFailed

- **Given** Job A 入队
- **And** Mock `IVlAnalyzer.ReAnalyzeAsync` 抛 `InvalidOperationException`
- **When** Worker 处理 Job A
- **Then** `MarkFailedAsync(jobA.Id, "InvalidOperationException ...", ct)` 被调用
- **Then** 不会调 `MarkCompletedAsync`

### UT-44 HostedService_WhenDbWriteFails_MarksJobFailed_AndContinues

- **Given** Job A 和 Job B 都入队
- **And** `MarkCompletedAsync(jobA.Id, ...)` 抛异常
- **When** Worker 处理
- **Then** Job A 被兜底 `MarkFailedAsync`
- **Then** Job B 仍能正常处理（不被 Job A 的异常打断）

### UT-45 HostedService_StopsGracefully_OnApplicationStopping

- **Given** Hosted Service 正在处理 Job
- **When** `stoppingToken` 被取消
- **Then** 当前 Job 的 VL 调用收到取消（不强制；只验证 Service 不抛 `OperationCanceledException`）
- **Then** Channel 停止接受新 Job

## gRPC 层单元测试

### UT-50 MistakeServiceImpl_StartReanalyzeJob_WithNonExistentItem_ThrowsNotFound

- **Given** gRPC `ReanalyzeMistakeItem` 被调用，Domain 返回 `ErrorCode = "NotFound"`
- **When** 调用 gRPC
- **Then** 抛 `RpcException(StatusCode.NotFound, "错题不存在")`

### UT-51 MistakeServiceImpl_StartReanalyzeJob_WithRejectedStatus_ThrowsFailedPrecondition

- **Given** Domain 返回 `ErrorCode = "FailedPrecondition"`
- **When** 调用 gRPC
- **Then** 抛 `RpcException(StatusCode.FailedPrecondition, ...)`

### UT-52 MistakeServiceImpl_StartReanalyzeJob_WithInvalidGuid_ThrowsInvalidArgument

- **Given** `request.Id = "not-a-guid"`
- **When** 调用 gRPC
- **Then** 抛 `RpcException(StatusCode.InvalidArgument, "错题 ID 格式无效")`

### UT-53 MistakeServiceImpl_StartReanalyzeJob_OnSuccess_ReturnsJobId

- **Given** Domain 返回 `Result.Success = true`，`Job.Id = "guid-1"`
- **When** 调用 gRPC
- **Then** 返回 `ReanalyzeMistakeItemResponse { Success = true, JobId = "guid-1" }`（`Item` 字段**不**设置）

### UT-54 MistakeServiceImpl_GetReanalyzeJobs_DelegatesToDomainService

- **Given** gRPC `GetReanalyzeJobs` 被调用，`request.JobIds = [id1, id2]`
- **And** Mock `IReanalyzeJobService.GetJobsAsync("reviewerId", [id1, id2], ct)` 返回 2 个 Job
- **When** 调用 gRPC
- **Then** 返回 `GetReanalyzeJobsResponse` 含 2 个 `ReanalyzeJobDto`
- **Then** `reviewer_id` 从 `ServerCallContext` 取（与现有其他 RPC 一致）

### UT-55 MistakeServiceImpl_GetActiveReanalyzeJobs_ReturnsAllActiveForReviewer

- **Given** gRPC `GetActiveReanalyzeJobs` 被调用
- **And** Mock `IReanalyzeJobService.GetActiveJobsAsync("reviewerId", ct)` 返回 3 个 Job
- **When** 调用 gRPC
- **Then** 返回 3 个 `ReanalyzeJobDto`

## BFF（teacher_portal）单元测试

### UT-60 MistakeController_StartReanalyzeJob_Returns202WithJobId

- **Given** gRPC mock 返回 `{ Success = true, JobId = "guid-1" }`
- **When** POST `/api/mistake/{id}/reanalyze?studentId=xxx` body=`{...}`
- **Then** 返回 `202 Accepted`，body = `{ success: true, data: { jobId: "guid-1" } }`

### UT-61 MistakeController_StartReanalyzeJob_WithoutStudentId_Returns400

- **Given** 缺 `studentId` query
- **When** POST
- **Then** 返回 `400`，`message = "缺少学生ID"`

### UT-62 MistakeController_StartReanalyzeJob_WhenNotFound_Returns404

- **Given** gRPC 返回 `NotFound`
- **When** POST
- **Then** 返回 `404`，`message = "错题不存在"`

### UT-63 MistakeController_StartReanalyzeJob_WhenFailedPrecondition_Returns422

- **Given** gRPC 返回 `FailedPrecondition`
- **When** POST
- **Then** 返回 `422`，`message` 对应 gRPC detail

### UT-64 MistakeController_GetReanalyzeJobs_ParsesCommaSeparatedIds

- **Given** `?ids=a,b,c`
- **When** GET `/api/mistake/reanalyze/jobs?ids=a,b,c`
- **Then** 解析为 `["a","b","c"]`，传给 gRPC
- **Then** 返回 200，data 为 gRPC 响应

### UT-65 MistakeController_GetReanalyzeJobs_WithoutIds_Returns400

- **Given** `?ids` 为空或缺
- **When** GET
- **Then** 返回 `400`，`message = "缺少 ids 参数"`

### UT-66 MistakeController_GetReanalyzeJobs_MoreThan100Ids_Returns400

- **Given** 101 个 id
- **When** GET
- **Then** 返回 `400`，`message = "ids 数量必须在 1-100 之间"`

### UT-67 MistakeController_GetActiveReanalyzeJobs_ReturnsList

- **Given** gRPC mock 返回 2 个 active Job
- **When** GET `/api/mistake/reanalyze/active-jobs`
- **Then** 返回 200，data 为 2 个 Job DTO

## 鉴权边界测试（关键）

### UT-70 Admin_Portal_DoesNotExpose_Reanalyze_Endpoints

- **Given** admin_portal BFF 路由表
- **When** 扫描 `api/admin/mistake` 路径
- **Then** **不**存在以下任一路由：
  - `{id}/reanalyze` (POST)
  - `reanalyze/jobs` (GET)
  - `reanalyze/active-jobs` (GET)
- **说明**：通过代码搜索（grep `admin_portal/backend`）或集成测试断言

### UT-71 Teacher_Portal_Exposes_All_Three_Endpoints

- **Given** teacher_portal BFF 路由表
- **When** 扫描 `api/mistake` 路径
- **Then** 存在以下 3 个路由

## 覆盖目标

| 层 | 目标 | 说明 |
|----|------|------|
| Domain `IReanalyzeJobService` | 行覆盖 ≥ 80% | 状态机 + Recover + Delete 是关键 |
| Hosted Service | 行覆盖 ≥ 70% | 时间相关逻辑用 Moq 时间源 |
| BFF Controller | 行覆盖 ≥ 80% | 3 个端点 + 鉴权 |
| gRPC Service | 错误码全覆盖 | 所有 ErrorCode → RpcException 映射 |

## 集成测试（pre-commit 之外）

- **DB 迁移验证**：`dotnet ef database update` 在 PostgreSQL 环境下成功
- **端到端 happy path**：起服务 → 调 `POST /api/mistake/{id}/reanalyze` → 轮询 → 验证 mistake_item.root_cause 更新
- **并发真实测试**：本地起服务，连续提交 5 个 Job，观察 worker 处理时间分布（应两两一组）

## 遗漏的测试场景

- VL 慢响应（>60s）的边界（用 Mock 模拟 Task.Delay 即可）
- Network blip 时 VL 重试（用 Mock 模拟 HttpRequestException 即可）
- 同一教师在 1 秒内对同一题提交 10 次（验证 Channel 不会 OOM）
- 数据库连接耗尽场景（暂不在范围内）
