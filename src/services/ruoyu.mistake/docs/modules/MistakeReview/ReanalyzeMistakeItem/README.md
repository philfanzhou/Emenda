# 错题根因重分析

教师在 PendingReview 或 Confirmed 错题上提交重分析请求；服务校验错题存在且有图片，将 Job 持久化为 Pending。Mistake 内部 HTTP 返回成功信封和 jobId，Teacher Portal 对浏览器返回 `202 + jobId`；前端按原有批量/活跃任务端点轮询。提交时的 `previous_root_cause` 是审计快照；真正执行时使用当时 Item 的根因，因而同题后继任务能看到前一任务已提交的结果。重分析不改变审核状态，也不支持取消。HTTP 路由和 DTO 保持现状；调用方授权边界由 [IKIZY0](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY0) 处理。

## 提交与查询契约

| 调用 | 当前行为 |
|---|---|
| Teacher Portal `POST /api/mistake/{id}/reanalyze?studentId=...` | 教师描述可选；BFF 从身份取得 reviewerId，检查科目权限，成功返回 202 和 `{success,data:{jobId}}`，不等待 VL |
| Mistake `POST /api/mistakes/{id}/reanalyze` | 请求体含 reviewerId、studentId、可选 teacherDescription；成功返回 200 信封、`success=true` 与 jobId，不调用 VL |
| Teacher Portal `GET /api/mistake/reanalyze/jobs?ids=...` | 一次查询 1–100 个 Job，只返回当前教师任务；未知或他人 ID 不回传 |
| Mistake `POST /api/mistakes/reanalyze/jobs` | 请求体含 reviewerId 和 jobIds；批量返回状态、结果、错误码及时间戳 |
| Teacher Portal `GET /api/mistake/reanalyze/active-jobs`、Mistake `GET /api/mistakes/reanalyze/active` | 只返回该 reviewer 的 Pending/Running，供页面刷新后恢复角标和轮询 |

提交时错题不存在返回 NotFound；状态不为 PendingReview/Confirmed 或无 SourceRegions 时拒绝创建 Job。批查询缺少 ID、超过 100 个或无 reviewerId 时返回失败响应。JobId 为随机 Guid；状态查询 DTO 包含 `jobId`、`itemId`、`studentId`、`status`、`newRootCause`、`errorMessage`、创建/开始/完成时间及 VL 耗时。重分析不修改 subject、grade、source_regions、review_status，也不调用 Student 服务。浏览器路由及身份策略由 Portal 主责；本切片不开放管理或匿名恢复入口。

## 状态与顺序

| 事件 | Job | Item 与日志 |
|---|---|---|
| 提交 | Pending，保存提交时根因快照 | 不变 |
| 领取 | 按 `created_at,id` 选择同题最早未终态 Job；进入 Running，随机 `owner_id`、90 秒租约、递增 fencing version 与 attempt；保存执行时根因和输入摘要 | 锁定并读取当前 Item，不写根因 |
| 续租 | 只有有效 owner/version 且未过期时按数据库时钟延长 90 秒 | 不变 |
| 成功 | 有效 owner/version/lease 才可转 Completed | 同事务核对执行输入、更新根因并写一条带 jobId 的 ProcessLog |
| 确定失败 | 同一栅栏下转 Failed，保存有限的原因码 | 根因不变；Item 仍存在时同事务写失败日志 |
| 崩溃或租约过期 | 到期后同一 Job 可被重领，最多三次（含首次）；达到上限转 Failed | 迟到执行者不能覆盖新结果或写日志 |
| 清理 | 只删除完成时间超过 30 天的 Completed/Failed | Pending/Running 不受清理影响 |

数据库事务 advisory lock 串行化领取。有效 Running 全局最多两个，两个 Host 实例共享此限制；各 Host 的两个 worker 只是本地上限。不同题可并行，同题后继等待前项终态。90 秒租约每 20 秒续租；失联时外部 VL 请求可能重复，但持久化终态由 `owner_id + fencing_version + lease_until` 栅栏保护。VL 正常确定性失败直接终态；原 VL 客户端的请求重试保持。停机停止领取，取消在途请求并有限等待；未终态 Job 留到租约过期，不把别的实例的 Running 失败化。

执行输入摘要覆盖 StudentId、SourceRegions（含图片路径）、执行时根因和审核状态。过期 Job 重领前和终态提交前都在行锁下核对首次领取保存的摘要；条目被删、被拒绝或输入被人工修改时 Job 失败，不刷新摘要或覆盖新值。摘要和原始图片输入不写 ProcessLog；错误只保存并记录有限原因码，不记录原始异常文本或密钥。成功日志保留根因结果和 jobId 以兼容现有审计。历史日志随 Item 外键级联删除，不保证删除后仍留存。

## 对外契约与验证

- `POST /api/mistakes/{id}/reanalyze` 仍只入队；`POST /api/mistakes/reanalyze/jobs` 与 `GET /api/mistakes/reanalyze/active` 的请求与响应字段不变。Teacher Portal 的用户路由和 5 秒轮询保持原样。
- Job 状态仍为 Pending、Running、Completed、Failed；同题可重复提交。失败时 Item 根因不变。终态保留 30 天。
- 数据库并发/栅栏和升级用 PostgreSQL 15 隔离库测试 `ReanalyzeLeasePostgresTests`、`ReadOnlyStartupPostgresTests`；Domain 入队与转发用 `ReanalyzeJobServiceTests`。在 `src/services/ruoyu.mistake/src` 运行 Release build、dotnet test；仓库快速检查见根 `AGENTS.md`。

## 部署边界

第五条迁移 `20260930000001_LeaseReanalyzeJobs` 引入租约与栅栏字段。所有旧 worker 必须停止后才可使用显式升级；旧无租约 Running 在迁移事务内变成 `Failed/upgrade_interrupted`，Item 存在时写一条关联日志。Pending 和已有终态保持，历史 ID 保持。新二进制只在精确第五历史结构上启动；旧二进制与新 schema 不兼容。停写、备份、升级、只读验库、启用和撤回步骤见[部署说明](../../../development/Deployment.md)及[故障排查](../../../development/Troubleshooting.md)。
