# 完成上传审核 (CompleteUploadReview)

> ⚠️ **此功能已废弃。** 目标流程中不存在独立的 CompleteUploadReview 步骤，每条审核通过时立即迁移。仅保留供 LegacyClean 运维兜底使用。

## 状态: Deprecated

## 一句话概括

完成一次上传记录的全部审核，将已确认错题的图片从 uploads/ 迁移至 mistakes/，并返回需从上传记录中移除的图片路径。

## 核心用户故事

1. 作为审核教师，我希望在完成某次上传记录的所有错题审核后，一键完成审核流程，使已确认的错题图片迁移到正式目录，未确认的图片从上传记录中移除

## 补充约束

- 必须所有错题均已完成审核（Confirmed 或 Rejected），不允许存在 PendingReview 状态的错题
- 图片迁移为 OSS Copy + Delete 操作，同一图片路径在多个错题中引用时只迁移一次（使用 pathMapping 缓存）
- 非 Confirmed 状态的错题图片仅从上传记录移除，不执行 OSS 迁移

## 关键验收条件摘要

- [ ] AC-01: 存在 PendingReview 错题时返回错误
- [ ] AC-02: 无错题记录时返回错误"未找到该上传记录的错题"
- [ ] AC-03: 全部审核完成后返回 Success=true，包含 studentId 和 removedImagePaths
- [ ] AC-04: Confirmed 错题的图片从 uploads/ 迁移至 mistakes/（OSS Copy+Delete）
- [ ] AC-05: 非 Confirmed 错题的图片仅加入 removedImagePaths，不迁移
- [ ] AC-06: MistakeValidationException 时返回 Success=false

## 明确范围外

- 不处理单个错题的审核（由 ReviewMistakeItem 负责）
- 不删除错题记录本身
- 不处理 OSS 迁移失败后的回滚（仅记录日志继续执行）

## 文档索引

- [02-SPEC - 详细需求规格](./02-SPEC.md)
- [03-DESIGN - 设计说明](./03-DESIGN.md)
- [04-TASKS - 任务清单](./04-TASKS.md)
- [05-TESTS - 测试计划](./05-TESTS.md)
- [06-CONVENTIONS - 约定规范](./06-CONVENTIONS.md)
