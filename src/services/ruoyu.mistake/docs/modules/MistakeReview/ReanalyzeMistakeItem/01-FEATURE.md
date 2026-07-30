# 重新分析错题根因 (ReanalyzeMistakeItem)

## 核心用户故事

作为教师，当我对 AI 自动生成的错题根因不满意时，我希望能够补充自己的描述并让 AI 结合之前的根因重新分析，以便获得更准确、更贴合实际教学场景的根因。

**本版本特性**：教师点击"AI 重新分析"后**立即**返回（不再阻塞 UI 60s+），后台异步执行；通过 5 秒轮询批量获取任务状态。期间教师可以继续审核其他错题、刷新页面、关闭浏览器；下次回来时如分析还在跑，角标会恢复并继续轮询。

## 补充约束

- **触发角色**：**仅教师**可触发（管理员**不可**调用此功能）
- **触发场景**：① 审核错题时（PendingReview 状态）② 查看已确认错题详情时（Confirmed 状态）
- **教师补充描述**：可选字段，留空表示"让 AI 重新思考但不附加额外上下文"
- **不重置审核状态**：重新分析只更新 `root_cause` 字段，**不改变** `ReviewStatus`（避免引入额外审核轮次）
- **异步执行**：提交即返回 JobId；后台 Hosted Service 串行调度 VL 调用，全局并发上限 = 2（写死）
- **VL 失败处理**：若 VL API 调用失败，**保留**原 `root_cause` 不变，Job 标 `Failed` + `error_message`，并写 ProcessLog
- **不支持取消**：教师一旦提交，必须等 Job 跑完（用户决策）
- **同题重复提交**：同 itemId 多次提交按时间顺序入队，串行执行；后续 Job 的 `previous_root_cause` 是上一个 Job 完成后的值
- **保留期**：Job 记录保留 30 天；过期由 `ReanalyzeJobCleanupService` 每日清理
- **审计必写**：每次重新分析必须写 ProcessLog（Action=Reanalyzed），details 含 `job_id` + 原 root_cause + 教师描述 + 新 root_cause（成功时）/ 失败原因（失败时）
- **状态查询**：批量端点 `GET /api/mistake/reanalyze/jobs?ids=...` 与活跃 Job 端点 `GET /api/mistake/reanalyze/active-jobs`
- **鉴权边界**：此功能**不**在 admin_portal 暴露（详见 [teacher_portal/MistakeList/02-SPEC.md](../../../../../../teacher_portal/docs/modules/MistakeReview/MistakeList/02-SPEC.md) AC-FR-10）

## 关键验收条件摘要

- [x] 接收重新分析请求（错题ID + 可选教师描述 + 审核人ID）
- [x] 同步路径完成校验：错题存在、状态允许、有图片；不通过则 4xx 返回
- [x] 校验通过后立即返回 202 + JobId（不调 VL）
- [x] 后台 Hosted Service 调度 Job，调用 VL 分析器，传入图片 + 原 root_cause + 教师描述
- [x] VL 返回新 root_cause 后更新 `mistake_item.root_cause` 和 `updated_at`；Job 标 `Completed`
- [x] 记录 ProcessLog（Action=Reanalyzed），details 含 `job_id`
- [x] VL 失败时保留原 root_cause，Job 标 `Failed` + error_message，仍写 ProcessLog（含失败原因）
- [x] 全局并发上限 = 2（写死，不可配置）；超出的 Job 在内存队列中等待
- [x] 服务重启时将 `Running` Job 标 `Failed`（崩溃恢复）
- [x] 仅 teacher_portal BFF 暴露 3 个端点，admin_portal **不**实现
- [x] Job 30 天后由 Cleanup Service 自动删除
- [x] 跨教师隔离：教师只能看自己提交的 Job

## 明确列出"范围外"

- 不修改 `ReviewStatus`（不重置审核流程）
- 不调用 Student 服务的任何接口（图片路径未变）
- 不修改 `subject` / `grade` / `source_regions`（仅 root_cause 是分析目标）
- 不提供频率限制（依赖 VL API 自身的成本控制 + Hosted Service 内部并发上限=2）
- 不支持批量重新分析（单条 API；批量场景通过多次提交 + 同题串行保证）
- 不向管理员暴露此功能
- **不**支持取消（用户决策）
- 不暴露"取消"端点
- 不支持 Job 优先级（按 created_at FIFO 调度）
- 不提供"撤回分析"功能（教师可再次提交覆盖）

## 文档索引

- [02-SPEC.md](./02-SPEC.md) - 功能规格与验收标准（含 FR / AC）
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档（数据模型 / Hosted Service / 状态机 / 数据流）
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试策略（含 70+ 个 UT 编号）
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 约定与规范（命名 / 错误码 / 日志）
