# 审核错题条目 (ReviewMistakeItem)

## 核心用户故事

作为教师，我希望能够审核学生提交的错题条目（确认或驳回），并查看/修改 AI 生成的错误根因，以便确认有效的错题进入练习流程，驳回无效的错题。

## 补充约束

- 只能审核 `PendingReview` 状态的错题条目
- 审核结果只能是 `Confirmed` 或 `Rejected`
- `root_cause` 字段在创建时由 VL 分析自动生成，老师审核时可修改
- 确认审核时，图片已在轮询阶段迁移到 `mistakes/`，无需额外 OSS 操作
- 驳回审核时，Mistake 将图片从 `mistakes/` 迁回 `uploads/`，删除 MistakeItem，并调用 Student 的 `CreateReturnedUploadRecord` 创建退回记录
- 可选在审核时更新 grade(1-12) 和 subject(1-9)

## 关键验收条件

- [x] 只能审核 PendingReview 状态的错题
- [x] 审核结果只能是 Confirmed 或 Rejected
- [x] 更新 ReviewStatus/ReviewerId/ReviewedAt/RootCause/UpdatedAt
- [x] 可选更新 grade 和 subject
- [x] root_cause 可由老师在审核时修改（AI 预填，老师可编辑）
- [x] 记录 ProcessLog（Action=ReviewConfirmed 或 ReviewRejected）
- [x] Confirmed 时图片已在轮询阶段迁移，无需额外 OSS 操作
- [x] Rejected 时删除 MistakeItem，迁移图片 mistakes/ → uploads/，调用 Student CreateReturnedUploadRecord
- [x] 验证失败时抛出 MistakeValidationException，gRPC 层转换为 InvalidArgument

## 范围外

- 批量审核（由 CompleteUploadReview 处理，但该接口已废弃）
- 审核通知推送
- 审核历史回溯（由 ProcessLog 覆盖）
- 驳回后的自动重新上传流程

## 文档索引

- [02-SPEC.md](./02-SPEC.md) - 功能规格与验收标准
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 约定与规范
