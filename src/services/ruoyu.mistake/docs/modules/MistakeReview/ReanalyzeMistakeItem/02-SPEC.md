# ReanalyzeMistakeItem — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：当教师对 AI 自动生成的错题根因不满意时，可触发 AI 重新分析，结合"原根因 + 教师补充描述"作为额外上下文，生成更准确的新根因。本版本将原同步阻塞式实现改造为**异步任务（Job）模型**：教师点击"AI 重新分析"后立即返回 JobId，由后台 Hosted Service 串行调度 VL 调用，前端通过 5 秒轮询批量获取任务状态来感知完成。仅教师可调用，admin 不支持。

**用户故事 1（审核时）**：作为教师，我在审核错题时如果觉得 AI 的根因不对，我可以输入"学生明显审错了题目，应该是计算失误"这样的描述，让 AI 结合图片和我提供的描述重新分析，**完成后我会在卡片和详情页看到新的根因。期间我可以继续审核其他错题、刷新页面、关闭浏览器，下次回来时分析若还在跑，状态会保留并继续轮询**。

**用户故事 2（已确认详情中）**：作为教师，我已确认的错题经过一段时间后发现根因分析太笼统，我可以进入错题详情页，重新触发 AI 分析来优化根因，**无需走驳回重传流程，也不必等它跑完才能做其他事**。

## 核心改造点（与上一版对比）

| 维度 | 旧（同步） | 新（异步 Job） |
|------|------------|----------------|
| 教师点击后行为 | 同步等待 60s+ | 立即关闭弹窗，Job 进入队列 |
| UI 状态 | 全程按钮置灰+spinner | 卡片角标"AI 重新分析中"+ 详情页状态区 |
| 刷新页面 | UI 状态丢失，server 任务继续跑但用户不可知 | 页面重载时拉取教师**在跑**的 Job 列表，恢复角标与轮询 |
| 多任务并行 | 同题可发起多次，同步阻塞会积压 | 全局并发=2，队列串行；同题多次请求按提交顺序入队 |
| 取消 | 无 | **不支持**（用户决策） |
| 失败行为 | root_cause 保留 | **一致**：root_cause 保留（不破坏业务连续性） |
| 历史 | 无 | 保留 30 天，便于审计/回顾 |
| 状态查询 | 无 | 批量端点 `GET /api/mistake/reanalyze/jobs?ids=...` |

## 功能要求清单（可独立测试）

### Job 模型

- [ ] FR-01：新增 `ReanalyzeJob` 实体（id / item_id / reviewer_id / student_id / teacher_description / previous_root_cause / status / error_message / new_root_cause / created_at / started_at / completed_at / vl_duration_ms）
- [ ] FR-02：`status` 枚举：`Pending` / `Running` / `Completed` / `Failed`（**不**含 `Cancelled`——不支持取消）
- [ ] FR-03：JobId 使用 Guid（与 mistake_item.id 一致），对前端完全 opaque

### 启动任务

- [ ] FR-04：`POST /api/mistake/{id}/reanalyze?studentId=xxx` 接收 `teacherDescription`（可选）
- [ ] FR-05：服务端在同步路径中完成以下校验，错则返回 4xx：
  - 错题存在（不存在 → 404 `"错题不存在"`）
  - 错题 `review_status ∈ {PendingReview, Confirmed}`（其他 → 422 `"只能对 PendingReview 或 Confirmed 状态的错题重新分析"`）
  - 错题有 `source_regions`（无图 → 422 `"错题无图片，无法重新分析"`）
  - `studentId` 必填（缺 → 400 `"缺少学生ID"`）
- [ ] FR-06：校验通过后**立即**返回 202 + `jobId`（不调用 VL），并将 Job 写入 DB（`Status=Pending`，记录 `previous_root_cause`）
- [ ] FR-06a：BFF 层（TeacherPortal）在调用 gRPC 前执行科目权限校验（与 ReviewItem 一致）：查询教师科目列表，若为空返回失败；获取错题的 subject，若教师无该科目权限返回失败
- [ ] FR-07：返回前**快照**当前 `root_cause` 到 Job 的 `previous_root_cause` 字段，用于审计
- [ ] FR-08：同题（itemId）可重复提交；同题在 `Pending` 或 `Running` 状态下再次提交时**不报错**，直接入队（同一 itemId 的 Job 在队列中按时间顺序串行执行）

### 后台处理

- [ ] FR-09：新增 `ReanalyzeJobHostedService`（IHostedService），启动时**写死** `MaxConcurrency = 2`（**不**走配置，不可配置），从 DB 拉取所有 `Pending` Job 入队
- [ ] FR-10：Worker 消费 Job 流程：
  1. `Status: Pending → Running`，写 `started_at`
  2. 调用 `IVlAnalyzer.ReAnalyzeAsync`（与旧版同实现）
  3. 成功：更新 mistake_item.root_cause + updated_at；`Status: Running → Completed`；写 `new_root_cause`、`completed_at`、`vl_duration_ms`；写 ProcessLog（含 jobId）
  4. 失败：mistake_item.root_cause **不变**；`Status: Running → Failed`；写 `error_message`、`completed_at`；写 ProcessLog（Failure，含 jobId）
- [ ] FR-11：Worker 全局并发上限 = 2；超出 2 的任务在内存队列中等待
- [ ] FR-12：每个 Job 由独立 Scope 处理（避免 DbContext 共享问题）

### 状态查询

- [ ] FR-13：`GET /api/mistake/reanalyze/jobs?ids=<id1>,<id2>,...` 批量查询（逗号分隔，最多 100 个 id）。gRPC request 显式带 `reviewer_id`（避免依赖 metadata 透传）
- [ ] FR-14：响应为 `[{ jobId, itemId, studentId, status, newRootCause, errorMessage, createdAt, startedAt, completedAt, vlDurationMs }, ...]`
- [ ] FR-15：仅返回当前 `reviewer_id == JWT.sub` 的 Job（鉴权）。**安全约束**：proto 字段是单一可信源，不允许从 gRPC metadata 头中读取 reviewer
- [ ] FR-16：未传入 `ids` 时返回 400；`ids` 数量为 0 或 > 100 时返回 400
- [ ] FR-17：`GET /api/mistake/reanalyze/active-jobs` 返回当前教师所有 `Pending`/`Running` Job（用于页面刷新后恢复角标）。gRPC request 显式带 `reviewer_id`

### 保留与清理

- [ ] FR-18：Job 记录保留 **30 天**（`completed_at + 30d` 之前允许查询，之后被清理任务删除）
- [ ] FR-19：新增 `ReanalyzeJobCleanupService`（IHostedService），周期执行清理：
  - **生产**：每 24 小时一次（启动后立即跑一次，之后每 24h）
  - **开发**：每 1 小时一次
  - 间隔**不**走配置（避免被错误调整）；开发/生产切换由 `IHostEnvironment.IsDevelopment()` 隐式决定
  - 删除 `status ∈ {Completed, Failed} AND completed_at < now - 30 days` 的所有 Job
  - 删除失败不抛异常，仅日志告警

### 鉴权

- [ ] FR-20：教师从 JWT 提取 `reviewer_id`（不接受请求体传入，防伪）；教师只能看/触发自己提交的 Job
- [ ] FR-21：admin_portal BFF **不**实现以上端点（沿用 FR-12 历史约束）

## 详细的验收标准（可自动验证）

### AC-FR-01：基本成功流程（异步版）

- **Given** 错题存在，`review_status = PendingReview`，`source_regions` 非空
- **When** 教师调用 `POST /api/mistake/{id}/reanalyze?studentId=xxx` body=`{"teacherDescription":"学生没读懂题意"}`
- **Then** 立即返回 `202`，body = `{ success: true, data: { jobId: "<guid>" } }`
- **Then** Job 在 DB 中存在，`Status = Pending`，`reviewer_id = JWT.sub`，`teacher_description = "学生没读懂题意"`，`previous_root_cause` 快照正确
- **Then** Hosted Service 取出 Job 调度执行，最终 `Status = Completed`，`new_root_cause` 等于 VL 返回值
- **Then** `mistake_item.root_cause` 更新为 VL 返回值，`updated_at` 更新
- **Then** ProcessLog 写入一条 `Action = Reanalyzed(7)`，details JSON 包含 `job_id`
- **Then** `mistake_item.review_status` 仍为 `PendingReview`（不变）

### AC-FR-02：批量状态查询

- **Given** 当前教师有 3 个 Job：job-a (Running) / job-b (Completed) / job-c (Failed)
- **When** `GET /api/mistake/reanalyze/jobs?ids=job-a,job-b,job-c`
- **Then** 200，data 为 3 个元素的数组
- **Then** job-a 返回 `{ status: "Running", newRootCause: null, ... }`
- **Then** job-b 返回 `{ status: "Completed", newRootCause: "<VL 返回>", ... }`
- **Then** job-c 返回 `{ status: "Failed", errorMessage: "<reason>", newRootCause: null, ... }`

### AC-FR-03：跨教师隔离

- **Given** 教师 A 提交了 job-a；教师 B 提交了 job-b
- **When** 教师 A 调用 `GET /api/mistake/reanalyze/jobs?ids=job-a,job-b`
- **Then** 响应**只**包含 job-a；job-b 被过滤掉（即便 ID 命中）
- **Then** 这不是"未找到"也不是错误，单纯不返回别人数据

### AC-FR-04：页面刷新恢复

- **Given** 教师提交了 1 个 Job，`Status = Running`
- **When** 教师刷新页面，调用 `GET /api/mistake/reanalyze/active-jobs`
- **Then** 响应包含该 Job，前端可以恢复角标与轮询

### AC-FR-05：VL 失败

- **Given** 错题存在，VL 模拟抛异常
- **When** Job 被 Hosted Service 处理
- **Then** `mistake_item.root_cause` **保持原值**
- **Then** Job `Status = Failed`，`error_message = "<VL 异常消息>"`
- **Then** ProcessLog 仍写入 `Action = Reanalyzed(7)`，details JSON 含 `job_id` + `status: "failed"` + 失败原因
- **Then** 后续 `GET .../jobs?ids=...` 返回 `status: "Failed"` + `errorMessage`

### AC-FR-06：同题重复提交排队

- **Given** 错题 X 当前已有 Job A 在 `Running`
- **When** 教师再次对错题 X 提交 Job B
- **Then** Job B 立即返回 `202`，写入 DB `Status = Pending`
- **Then** Hosted Service 队列中：A 完成后才执行 B；B 完成时使用 A 完成后的 root_cause 作为 previous_root_cause
- **Then** 教师前端角标在 Job A 完成时**先**刷新一次，Job B 开始时**再**刷新一次（最终值以 B 为准）

### AC-FR-07：30 天保留

- **Given** Job 已 `Completed`，`completed_at = 2026-05-01 00:00:00 UTC`
- **And** CleanupService 任意一次执行（生产周期 24h，开发周期 1h）
- **When** CleanupService 跑 `DELETE WHERE completed_at < now - 30 days`
- **Then** 当 `now >= 2026-05-31 00:00:00 UTC` 时该 Job 被删除
- **And** 30 天内（即 `now < 2026-05-31 00:00:00 UTC`）查询仍可命中

### AC-FR-08：状态不允许

- **Given** 错题 `review_status = Rejected`
- **When** 教师调用 `POST /api/mistake/{id}/reanalyze?studentId=xxx`
- **Then** 同步返回 `422`（沿用旧版的 gRPC 错误码），`message = "只能对 PendingReview 或 Confirmed 状态的错题重新分析"`
- **Then** 不创建 Job，不调用 VL

### AC-FR-09：错题无图

- **Given** 错题存在但 `source_regions` 为空
- **When** 教师调用 `POST /api/mistake/{id}/reanalyze?studentId=xxx`
- **Then** 同步返回 `422`，`message = "错题无图片，无法重新分析"`
- **Then** 不创建 Job

### AC-FR-10：错题不存在

- **Given** 错题 ID 不存在
- **When** 教师调用 `POST /api/mistake/{id}/reanalyze?studentId=xxx`
- **Then** 同步返回 `404`，`message = "错题不存在"`
- **Then** 不创建 Job

### AC-FR-11：Hosted Service 并发上限

- **Given** DB 中有 5 个 `Pending` Job（不同 item）
- **And** `MaxConcurrency = 2`
- **When** Hosted Service 启动
- **Then** 同一时刻**最多**有 2 个 Job 处于 `Running`，其余 3 个处于 `Pending`
- **Then** 任意 Running 完成后，下一个 Pending 立即被取走

### AC-FR-12：服务重启时的崩溃恢复

- **Given** DB 中有 2 个 `Running` 状态的 Job（来自上次进程崩溃）
- **When** Hosted Service 启动
- **Then** 这 2 个 Job 在启动时**立即**被标记为 `Failed`，`error_message = "服务重启导致任务中断"`
- **Then** 之后才拉取所有 `Pending` Job 入队执行
- **Then** `mistake_item.root_cause` 不会被更新（这些 Job 都没真正完成 VL 调用）

### AC-FR-12a：原子性（item + job + process_log 一致性）

- **Given** Worker 调用 `MarkCompletedAsync(jobId, newRootCause, ...)`
- **When** 任何一步（item update / job status change / process_log insert）发生异常
- **Then** **三表写入要么全部成功，要么全部回滚**（`IUnitOfWork.SaveChangesAsync` 是唯一的提交点）
- **Then** 不会出现"item 已更新但 job 还停留在 Running"或"job 标 Failed 但 log 缺失"等中间态
- **说明**：核心实现是 `IMistakeItemRepository.SetRootCauseDeferredAsync`（不立即 SaveChanges）+ `IReanalyzeJobRepository.UpdateStatusAsync`（不立即 SaveChanges）+ `IProcessLogRepository.AddAsync`（本就不 SaveChanges），由 ReanalyzeJobService 集中一次性 SaveChanges

### AC-FR-13：批查询入参校验

- **Given** 缺少 `ids` 参数
- **When** `GET /api/mistake/reanalyze/jobs`
- **Then** 返回 `400`，`message = "缺少 ids 参数"`

- **Given** `ids=` 长度 = 0 或 > 100
- **When** 调该端点
- **Then** 返回 `400`，`message = "ids 数量必须在 1-100 之间"`

### AC-FR-14：admin_portal 隔离

- **Given** admin_portal BFF 路由表
- **When** 扫描 `api/admin/mistake` 路径
- **Then** **不**存在 `reanalyze` / `reanalyze/jobs` / `reanalyze/active-jobs` 路由
- **说明**：通过代码搜索（grep）或集成测试断言验证

### AC-FR-15：JobId 不可预测

- **Given** 业务方发起 1 次提交
- **Then** 返回的 jobId 是 `Guid.NewGuid()` 生成（v4 随机），与数据库行号、自增计数、提交时间等可观测维度无关
- **说明**：使用 .NET `Guid.NewGuid()` 即可满足；不要改成有序 Guid 或可枚举值

## 非功能需求

| 类别 | 需求 |
|------|------|
| **性能** | 提交 API P95 < 200ms（不包含 VL）；批量状态查询 P95 < 300ms（100 个 id） |
| **可观测性** | 每次 Job 状态切换写 INFO 级日志（含 jobId、itemId、vl_duration_ms）；Worker 启动/停止写 INFO |
| **审计** | 成功/失败都必须写 ProcessLog（`Action=Reanalyzed(7)`），details JSON 必含 `job_id` |
| **一致性** | VL 失败时 root_cause 不更新；Job 进入 Running 之前**不**调用 VL；进入 Running 后**不**取消 |
| **可靠性** | 服务重启自动将 `Running` 标 `Failed`；并发上限写死 2，不可被错误配置改成 1 或 0 |
| **可移植性** | 复用现有 `IVlAnalyzer` 接口、`ProcessAction` 枚举（值 7） |

## 测试策略

- **覆盖率目标**：
  - Domain 层 `IReanalyzeJobService` 行覆盖 ≥ 80%
  - Hosted Service 行覆盖 ≥ 70%（时间敏感逻辑用 Moq 时间源）
  - BFF Controller 行覆盖 ≥ 80%
- **必测错误路径**：
  - 错题不存在 / 状态不允许 / 无图（同步路径）
  - VL 抛异常（异步路径）
  - DbContext 写库失败（异步路径）
  - 服务重启时 `Running → Failed` 的崩溃恢复
  - 跨教师 ID 过滤
  - `ids` 入参边界
- **必测成功路径**：
  - 同题 2 个 Job 串行执行（A 完成 → B 开始）
  - 5 个 Job 全局并发 ≤ 2
  - 30 天清理（用时钟 Moq 模拟）
- **测试环境要求**：
  - Domain / Service / HostedService：`xUnit + Moq + FluentAssertions`，必要时 `InMemory` EF Core
  - BFF：`xUnit + Moq`，Mock gRPC client
- **gRPC 集成测试**：通过 `MistakeServiceImpl` 验证 StartReanalyzeJob 错误码转换

## 与相关功能的对比

| 功能 | 触发者 | 触发状态 | 输入 | 输出 | 状态变更 | 是否阻塞 UI |
|------|--------|----------|------|------|----------|------------|
| **ReviewMistakeItem** | 教师 | PendingReview | 审核结果 | 更新后 item | Confirmed/Rejected | 否（一直如此） |
| **ReanalyzeMistakeItem（旧）** | 教师 | PendingReview/Confirmed | 教师描述 | 新 root_cause | 不变 | **是**（60s+） |
| **ReanalyzeMistakeItem（新）** | 教师 | PendingReview/Confirmed | 教师描述 | JobId（异步） | 不变 | **否**（提交即返回） |
| **Student.ManualAnalyze** | 管理员 | UploadRecord 任意 | 无 | VL 原始结果 | 无 | 不适用 |

> 新版与 ReviewMistakeItem 互不冲突：可在同一 PendingReview 流程中先提交 reanalysis Job 再审核；也可在 Confirmed 后再优化根因。
