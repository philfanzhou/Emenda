# ReanalyzeMistakeItem — 约定与规范 (CONVENTIONS)

## 命名约定

| 元素 | 规范 | 示例 |
|------|------|------|
| gRPC RPC（启动） | `ReanalyzeMistakeItem` | 沿用旧名（语义变为"仅入队"） |
| gRPC RPC（批量状态） | `GetReanalyzeJobs` | |
| gRPC RPC（活跃 Job） | `GetActiveReanalyzeJobs` | |
| gRPC Request/Response | PascalCase | `ReanalyzeMistakeItemRequest` |
| gRPC DTO | `ReanalyzeJobDto` | |
| gRPC field | `snake_case` | `job_id`、`new_root_cause` |
| 枚举 - gRPC | `SCREAMING_SNAKE_CASE` | `REANALYZE_JOB_STATUS_PENDING` |
| 枚举 - C# | PascalCase | `ReanalyzeJobStatus.Pending` |
| C# Method | PascalCase + Async 后缀 | `StartJobAsync`、`MarkCompletedAsync` |
| C# Field | PascalCase | `ReviewerId`、`PreviousRootCause` |
| C# 状态常量（写死） | `private const int` | `MaxConcurrency = 2` |
| BFF endpoint | `POST/GET /api/mistake/...` | `POST /api/mistake/{id}/reanalyze` |
| BFF Response | PascalCase（JSON camelCase） | `ReanalyzeJobStatusDto` |
| 错误信息 | 中文（业务域） | "只能对 PendingReview 或 Confirmed 状态的错题重新分析" |
| 日志消息 | 英文（系统日志） | "ReanalyzeJob hosted service started, maxConcurrency=2" |
| 清理任务周期 | `private static readonly TimeSpan` | `ProductionInterval = TimeSpan.FromHours(24)` |
| 保留期 | `private const int` | `RetentionDays = 30` |
| JobId | `Guid` | `Job.Id = Guid.NewGuid()` |

## 错误信息规范（同步路径）

| 触发条件 | 错误信息 | gRPC Code | BFF HTTP |
|----------|----------|-----------|----------|
| ID 为空 | "错题 ID 不能为空" | `InvalidArgument` | 400 |
| ID 格式无效 | "错题 ID 格式无效" | `InvalidArgument` | 400 |
| 错题不存在 | "错题不存在" | `NotFound` | 404 |
| 状态不允许 | "只能对 PendingReview 或 Confirmed 状态的错题重新分析" | `FailedPrecondition` | 422 |
| 无图片 | "错题无图片，无法重新分析" | `FailedPrecondition` | 422 |
| 缺 studentId | "缺少学生ID" | （BFF 层校验） | 400 |
| 缺 ids 参数 | "缺少 ids 参数" | （BFF 层校验） | 400 |
| ids 数量非法 | "ids 数量必须在 1-100 之间" | （BFF 层校验） | 400 |
| 未识别教师身份 | "未识别教师身份" | （BFF 层校验） | 401 |

## 异步失败错误信息

| 触发条件 | `Job.ErrorMessage` | `mistake_item.root_cause` |
|----------|---------------------|----------------------------|
| VL API 异常 | VL 异常消息原文 | **不变** |
| VL 返回空结果 | "VL 未返回根因" | **不变** |
| DbContext 写库失败 | "内部异常: <message>" | **不变** |
| 服务重启（崩溃恢复） | "服务重启导致任务中断" | **不变** |

## 日志规范

```csharp
// 启动
_logger.LogInformation(
    "ReanalyzeJobHostedService started: maxConcurrency={MaxConcurrency}", 2);

// 启动崩溃恢复
_logger.LogInformation(
    "ReanalyzeJobHostedService: recovered {Count} stale running jobs", recoveredCount);

// Worker claim
_logger.LogDebug("ReanalyzeJobHostedService: claimed {Count} pending jobs", count);

// Worker 状态切换
_logger.LogInformation(
    "ReanalyzeJob status transition: JobId={JobId}, ItemId={ItemId}, From={From}, To={To}, VlDurationMs={VlDurationMs}",
    jobId, itemId, "Pending", "Completed", durationMs);

// Cleanup
_logger.LogInformation(
    "ReanalyzeJobCleanupService: deleted {Count} expired jobs (cutoff={Cutoff:o})",
    deletedCount, cutoff);

// VL 失败
_logger.LogWarning(
    "ReanalyzeJob: VL call failed. JobId={JobId}, ItemId={ItemId}, Reason={Reason}",
    jobId, itemId, reason);

// 批量查询
_logger.LogDebug(
    "GetReanalyzeJobs: ReviewerId={ReviewerId}, Count={Count}",
    reviewerId, jobIds.Count);
```

**禁止**：
- 禁止记录 root_cause 完整内容（可能含敏感学生信息）
- 禁止记录 teacher_description 完整内容（可能含敏感教学反馈）
- 仅记录**长度**和**是否提供**即可
- 禁止记录 `new_root_cause`（同上）

## 状态机约定

| 转换 | 触发者 | 字段写入 |
|------|--------|----------|
| → Pending | StartJobAsync | created_at |
| Pending → Running | MarkRunningAsync | started_at |
| Running → Completed | MarkCompletedAsync | new_root_cause, completed_at, vl_duration_ms |
| Running → Failed | MarkFailedAsync | error_message, completed_at |
| Running → Failed（恢复） | RecoverStaleRunningJobsAsync | error_message = "服务重启导致任务中断", completed_at |
| Pending → Failed（极端） | MarkFailedAsync（理论允许，标记 worker 启动前崩溃） | error_message, completed_at |

**禁止**：
- 禁止从 Pending 直接到 Completed（业务上无意义：还没跑完 VL 怎么会完成）
- 禁止从 Completed/Failed 回到 Pending/Running
- 禁止用户主动 Cancelled（不支持取消）

## VL Prompt 规范（沿用旧版）

- 必须**明确说明是重新分析**（"You are re-analyzing..."）
- 必须**引用原 root_cause** 作为参考
- 教师描述若有，**作为补充上下文**（"additional context"）
- 输出**仅** JSON `{"rootCause": "..."}`，无其他文字
- 中文根因，**不超过 50 字符**（与自动分析一致）
- 不修改 subject/grade（重新分析仅关注根因）

## 鉴权边界规范

| 调用方 | 允许 | 说明 |
|--------|------|------|
| Teacher Portal BFF | ✅ 允许 3 个端点 | 鉴权从 JWT 取 reviewer_id |
| Admin Portal BFF | ❌ **不实现** | 业务方硬性约束 |
| Student Portal BFF | ❌ 不适用 | 学生不可重新分析 |
| Mistake 服务 gRPC | ✅ 暴露 | 不鉴权角色，由 BFF 端点契约保证 |

## 关键设计规范

1. **不重置 ReviewStatus**：重新分析 ≠ 重新审核
2. **不持久化 teacher_description 到 mistake_item**：仅在 Job 表保留（用于审计）
3. **不修改 subject/grade/source_regions**：仅更新 root_cause
4. **VL 失败保留原值**：业务连续性 > 提示错误
5. **必写 ProcessLog**：审计要求（`Action=Reanalyzed(7)`）
6. **不调用 Student 服务**：图片路径不变
7. **不引入新依赖**：复用现有 IVlAnalyzer + MistakeItemModel.RootCause
8. **并发上限写死 2**：常量 `MaxConcurrency = 2` 在 Hosted Service 静态字段，**不**走配置
9. **不支持取消**：状态机无 Cancelled 分支
10. **批量查询而非单 Job 轮询**：前端 5s 一次拉所有活跃 Job

## 兼容性规范

- 旧 `ReanalyzeMistakeItem` gRPC 方法**保留方法名**，但**改造**响应：
  - 移除 `MistakeItemDto item` 字段
  - 新增 `string job_id` 字段
  - 旧的 teacher_portal BFF `POST /api/mistake/{id}/reanalyze` 同步返回 item 的客户端逻辑必须改造为接收 jobId
- 旧 `MistakeServiceImpl.ReanalyzeMistakeItem` 内联调 VL 的代码**移除**，改为调 `IReanalyzeJobService.StartJobAsync`
- proto tag 7（ProcessAction）已存在（PROCESS_ACTION_REANALYZED=7），**不**新增
- 新 BFF 端点为**纯新增**（GetReanalyzeJobs / GetActiveReanalyzeJobs）

## 安全规范

- **PII 处理**：root_cause / teacher_description 可能含 PII，仅在 BFF 透传，不写日志
- **注入防护**：teacher_description 拼入 prompt 之前**不**做转义（VL 模型按自然语言处理），但 BFF 端应对长度做上限校验（≤ 500 字）
- **认证**：BFF 端点从 JWT 提取 reviewer_id，不接受请求体传入（防伪）
- **跨教师隔离**：所有查询 SQL 都带 `WHERE reviewer_id = @currentReviewerId`；写操作由 reviewer_id 字段强制归属
- **JobId 不可预测**：使用 `Guid.NewGuid()`，避免业务方枚举/猜测
