# 错题审核 (ReviewMistakeItem)

## 功能名称和一句话概括

错题审核 — 对处于待审核状态的错题条目进行确认或驳回操作，审核人可同时修正错题的年级和学科归属。

## 核心用户故事

1. **老师审核错题**：作为老师，我希望对提交的错题进行逐条审核，确认错题分类和年级归属正确后通过审核，或者驳回不符合要求的错题条目。

2. **审核人修正错题属性**：作为审核人，在审核过程中发现错题的年级或学科归属有误时，我可以直接修正这些字段，无需退回重新提交。

## 关键验收条件摘要

- AC-1：审核操作只能应用于 `PendingReview` 状态的错题，否则返回错误。
- AC-2：审核结果只能是 `Confirmed` 或 `Rejected`，传入其他值应在 gRPC 层被拒绝。
- AC-3：审核成功后，错题的 `review_status`、`reviewer_id`、`reviewed_at`、`review_comment` 字段更新为传入值。
- AC-4：可选择性更新 `grade`（有效范围 1-12）和 `subject`（有效范围 1-9），传入 0 或无效值时不更新。
- AC-5：审核操作应记录操作日志（`ProcessAction.ReviewConfirmed` 或 `ProcessAction.ReviewRejected`）。

## 明确列出"范围外"（不做什么）

- 不处理批量审核（每次仅审核单条错题）。
- 不涉及审核工作流（分配、撤回、二审）。
- 不处理 OSS 图片迁移（图片迁移由 `CompleteUploadReview` 负责）。
- 不验证审核人权限（由上层 API 网关负责）。

## 文档索引

- [SPEC.md](./SPEC.md) — 详细需求与验收标准清单
- [DESIGN.md](./DESIGN.md) — 架构设计、接口签名与数据流
- [TASKS.md](./TASKS.md) — 开发/验证任务列表
- [TESTS.md](./TESTS.md) — 单元测试与集成测试计划
- [CONVENTIONS.md](./CONVENTIONS.md) — 命名约定、日志安全与代码风格