# 审核错题条目 (ReviewMistakeItem)

## 核心用户故事

作为教师，我希望能够审核学生提交的错题条目（确认或驳回），以便确认有效的错题进入练习流程，驳回无效的错题要求重新上传。审核完成后，系统自动完成后续编排：确认时迁移图片并通知学生服务移除上传记录中的图片引用，驳回时删除错题条目；当上传记录下无待审核条目时，自动标记上传记录为已退回。

## 补充约束

- 只能审核 `PendingReview` 状态的错题条目
- 审核结果只能是 `Confirmed` 或 `Rejected`
- 确认审核时，gRPC 层会执行完整后审核编排：
  1. 图片迁移：将 `uploads/` 前缀的图片路径迁移到 `mistakes/`（OSS Copy+Delete）
  2. 调用学生服务 `RemoveImagesFromRecord` 从上传记录移除已迁移图片
  3. 检查该上传记录下是否还有待审核条目，若无则调用 `MarkUploadRecordAsReturned`
- 驳回审核时，gRPC 层会删除该 MistakeItem，并检查该上传记录下是否还有待审核条目，若无则调用 `MarkUploadRecordAsReturned`
- 可选在审核时更新 grade(1-12) 和 subject(1-9)

## 关键验收条件

- [x] 只能审核 PendingReview 状态的错题
- [x] 审核结果只能是 Confirmed 或 Rejected
- [x] 更新 ReviewStatus/ReviewerId/ReviewedAt/ReviewComment/UpdatedAt
- [x] 可选更新 grade 和 subject
- [x] 记录 ProcessLog（Action=ReviewConfirmed 或 ReviewRejected）
- [x] Confirmed 时迁移图片路径 uploads/ → mistakes/（OSS Copy+Delete）
- [x] 收集所有 SourceImagePath 到 removed_image_paths
- [x] 迁移后更新 MistakeItemModel 的 SourceImagePath
- [x] Confirmed 时调用学生服务 RemoveImagesFromRecord 移除上传记录中的图片引用
- [x] Rejected 时删除该 MistakeItem
- [x] 审核后检查该上传记录下是否还有待审核条目，若无则调用 MarkUploadRecordAsReturned
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
