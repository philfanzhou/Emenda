# ReanalyzeMistakeItem — 任务清单 (TASKS)

> **迁出说明（2026-09-24）**：本文中的 `src/admin_portal` / `admin_portal` 路径已随 Admin Portal 迁出为外部仓库 [Ruoyu.Admin](https://github.com/philfanzhou/Ruoyu.Admin)（[ADR-0010](../../../../../../../docs/adr/0010-extract-ruoyu-admin.md)），在本仓库已不存在。本文保留原始描述作为历史记录，不作为当前运行手册使用。


> 任务按依赖顺序排列。**所有任务均未开始**（新功能，旧版已落地）。

## 1. 文档先行

- [ ] TASK-01: 更新 `01-FEATURE.md`（核心用户故事 + 关键约束）
- [ ] TASK-02: 重写 `02-SPEC.md`（异步 Job 模型） ✅
- [ ] TASK-03: 重写 `03-DESIGN.md`（数据模型 + HostedService + 状态机） ✅
- [ ] TASK-04: 重写 `04-TASKS.md`（本文件） ✅
- [ ] TASK-05: 重写 `05-TESTS.md`（Job 状态机 / HostedService / 批量状态端点 / 30 天清理 / 崩溃恢复）
- [ ] TASK-06: 重写 `06-CONVENTIONS.md`（命名 / 错误码 / 日志）
- [ ] TASK-07: 更新 `teacher_portal/MistakeList/02-SPEC.md`（前端可见 Job 状态）
- [ ] TASK-08: 更新 `teacher_portal/Integration/MistakeService/03-DESIGN.md`（集成示例 + 时序图）
- [ ] TASK-09: 更新 `teacher_portal/Integration/MistakeService/02-SPEC.md`（gRPC 方法清单更新）

## 2. Proto 改造

- [ ] TASK-10: `mistake.common.proto` 新增 `ReanalyzeJobStatus` 枚举（Pending/Running/Completed/Failed）
- [ ] TASK-11: `mistake.proto` 改造 `ReanalyzeMistakeItemResponse`（移除 `item` 字段，新增 `job_id`）
- [ ] TASK-12: `mistake.proto` 新增 `ReanalyzeJobDto` message
- [ ] TASK-13: `mistake.proto` 新增 `GetReanalyzeJobsRequest/Response` + RPC
- [ ] TASK-14: `mistake.proto` 新增 `GetActiveReanalyzeJobsRequest/Response` + RPC
- [ ] TASK-15: 重新生成 C# proto 代码（`dotnet build` 触发）

## 3. Domain 层

- [ ] TASK-16: `Domain/Models/ReanalyzeJobModel.cs` 新建
- [ ] TASK-17: `Domain/Repositories/IReanalyzeJobRepository.cs` 新建
- [ ] TASK-18: `Database/Repositories/ReanalyzeJobRepository.cs` 新建
- [ ] TASK-19: `Domain/Errors/ReanalyzeJobError.cs` 新建（含 ErrorCode 枚举）
- [ ] TASK-20: `Domain/Services/IReanalyzeJobService.cs` 新建
- [ ] TASK-21: `Domain/Services/ReanalyzeJobService.cs` 新建（含 StartJob/GetJobs/GetActiveJobs/状态机/Recover/DeleteExpired）

## 4. EF Core / 迁移

- [ ] TASK-22: `Database/Entities/ReanalyzeJobEntity.cs` 新建
- [ ] TASK-23: `Database/MistakeDbContext.cs` 注册 `DbSet<ReanalyzeJobEntity>` + 索引配置
- [ ] TASK-24: `Database/Migrations/YYYYMMDDHHMMSS_AddReanalyzeJob.cs` 新建（PostgreSQL provider）
- [ ] TASK-25: 验证 `dotnet ef migrations add` 后 SQL 正确性（手动检查 `Up`/`Down`）

## 5. Hosted Services

- [ ] TASK-26: `Host/Services/ReanalyzeJobHostedService.cs` 新建
  - 常量 `MaxConcurrency = 2`（写死，private static readonly int）
  - Channel-based in-memory queue
  - 启动时调 `RecoverStaleRunningJobsAsync`
  - 主循环 `ClaimPendingJobsAsync` 拉 DB 入 Channel
  - 2 个 worker Task 并发消费
- [ ] TASK-27: `Host/Services/ReanalyzeJobCleanupService.cs` 新建
  - 启动后立即跑一次，之后每 24h 跑一次（生产），开发态每 1h
  - 调 `IReanalyzeJobService.DeleteExpiredJobsAsync`
- [ ] TASK-28: `Host/Program.cs` 注册两个 HostedService

## 6. gRPC Service 层

- [ ] TASK-29: `Service/MistakeServiceImpl.cs` 改造 `ReanalyzeMistakeItem`（同步路径：仅校验 + 调 StartJobAsync，返回 jobId 而非 item）
- [ ] TASK-30: `Service/MistakeServiceImpl.cs` 新增 `GetReanalyzeJobs` override
- [ ] TASK-31: `Service/MistakeServiceImpl.cs` 新增 `GetActiveReanalyzeJobs` override
- [ ] TASK-32: `Service/MistakeServiceImpl.cs` 新增 `MapJobDto` 私有方法

## 7. Teacher Portal BFF

- [ ] TASK-33: `Models/AuthModels.cs` 新增 `StartReanalyzeJobResponse` / `ReanalyzeJobStatusDto`
- [ ] TASK-34: `Controllers/MistakeController.cs` 改造 `ReanalyzeMistake` 为 `StartReanalyzeJob`（返回 202 + jobId）
- [ ] TASK-35: `Controllers/MistakeController.cs` 新增 `GetReanalyzeJobs` 端点
- [ ] TASK-36: `Controllers/MistakeController.cs` 新增 `GetActiveReanalyzeJobs` 端点

## 8. Teacher Portal Frontend

- [ ] TASK-37: `types/index.ts` 新增 `StartReanalyzeJobResponse` / `ReanalyzeJobStatus` 类型
- [ ] TASK-38: `composables/useReanalyzePolling.ts` 新建（5s 批量轮询 composable）
- [ ] TASK-39: `components/ReviewDetail.vue` 改造 `handleReanalyze`（提交即关闭 + 加入轮询集合）
- [ ] TASK-40: `components/RecordDetail.vue` 同上
- [ ] TASK-41: `components/RecordCard.vue` 新增"AI 重新分析中"角标（接收 `reanalyzeJobStatus` prop）
- [ ] TASK-42: `components/RecordList.vue` 在 `onMounted` + 切换分类时拉取 `active-jobs`，分发给卡片 + 启动轮询
- [ ] TASK-43: 前端 detail 端在 `loadDetail` 后检查 `itemId` 是否在轮询集合，是则拉取最新 status

## 9. 测试

- [ ] TASK-44: `ReanalyzeJobServiceTests.cs`（Domain 层）：StartJob 校验路径、状态机、跨教师隔离、Recover、DeleteExpired
- [ ] TASK-45: `ReanalyzeJobHostedServiceTests.cs`：并发上限 2、启动崩溃恢复、Channel 行为
- [ ] TASK-46: `ReanalyzeJobCleanupServiceTests.cs`：调用 `DeleteExpiredJobsAsync`，24h 周期
- [ ] TASK-47: `MistakeServiceImplRpcTests.cs` 改造：`ReanalyzeMistakeItem` 改为同步返回 jobId；新增 `GetReanalyzeJobs` / `GetActiveReanalyzeJobs` 测试
- [ ] TASK-48: 旧 `ReanalyzeMistakeItemTests.cs` 重写为 `StartReanalyzeJobTests.cs`（沿用 happy path 思路但断言 jobId 而非 item）
- [ ] TASK-49: `MistakeControllerTests.cs`（teacher_portal）改造：3 个端点
- [ ] TASK-50: 验证 admin_portal **不**含 re-analyze 路由（grep `admin_portal/backend` 路径）

## 10. 验证

- [ ] TASK-51: 运行 `./tests/integration/scripts/pre-commit.sh --skip-docker --skip-e2e --skip-integration`
- [ ] TASK-52: 3 个前端构建 + 2 个 BFF 后端 + Mistake 服务构建
- [ ] TASK-53: 检查覆盖率（Domain ≥ 80% / Hosted ≥ 70% / BFF ≥ 80%）
- [ ] TASK-54: 手动验证：刷新页面后角标恢复（本地起服务，跑流程）
