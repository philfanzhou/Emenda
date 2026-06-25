# 重新分析错题根因 (ReanalyzeMistakeItem)

## 核心用户故事

作为教师，当我对 AI 自动生成的错题根因不满意时，我希望能够补充自己的描述并让 AI 结合之前的根因重新分析，以便获得更准确、更贴合实际教学场景的根因。

## 补充约束

- **触发角色**：**仅教师**可触发（管理员**不可**调用此功能）
- **触发场景**：① 审核错题时（PendingReview 状态）② 查看已确认错题详情时（Confirmed 状态）
- **教师补充描述**：可选字段，留空表示"让 AI 重新思考但不附加额外上下文"
- **不重置审核状态**：重新分析只更新 `root_cause` 字段，**不改变** `ReviewStatus`（避免引入额外审核轮次）
- **VL 失败处理**：若 VL API 调用失败，**保留**原 `root_cause` 不变，仅记录 ProcessLog
- **审计必写**：每次重新分析必须写 ProcessLog（Action=Reanalyzed），details 含原 root_cause + 教师描述 + 新 root_cause
- **鉴权边界**：此功能**不**在 admin_portal 暴露（详见 [teacher_portal/MistakeList/02-SPEC.md](../../../teacher_portal/docs/modules/MistakeReview/MistakeList/02-SPEC.md) AC-FR-10）

## 关键验收条件摘要

- [x] 接收重新分析请求（错题ID + 可选教师描述 + 审核人ID）
- [x] 验证错题存在；不存在时返回 `NotFound`
- [x] 验证根因状态允许重新分析（PendingReview 或 Confirmed）；其他状态返回 `FailedPrecondition`
- [x] 调用 VL 分析器，传入图片 + 原 root_cause + 教师描述
- [x] VL 返回新 root_cause 后更新 `mistake_item.root_cause` 和 `updated_at`
- [x] 记录 ProcessLog（Action=Reanalyzed）
- [x] VL 失败时保留原 root_cause，返回 success=false
- [x] 仅 teacher_portal BFF 暴露此端点，admin_portal **不**实现

## 明确列出"范围外"

- 不修改 `ReviewStatus`（不重置审核流程）
- 不调用 Student 服务的任何接口（图片路径未变）
- 不修改 `subject` / `grade` / `source_regions`（仅 root_cause 是分析目标）
- 不提供频率限制（依赖 VL API 自身的成本控制）
- 不支持批量重新分析
- 不向管理员暴露此功能
- 不存储 `teacher_description` 到 mistake_item（仅 ProcessLog 保留审计）

## 文档索引

- [02-SPEC.md](./02-SPEC.md) - 功能规格与验收标准
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试策略
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 约定与规范
