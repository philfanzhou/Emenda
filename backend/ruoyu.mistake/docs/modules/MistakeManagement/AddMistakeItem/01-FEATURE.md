# 手动添加错题 (AddMistakeItem)

## 核心用户故事

作为教师，我希望能够手动添加错题条目，以便将学生试卷中截取的错题区域录入系统进行后续审核和练习。

## 补充约束

- 新增的错题条目自动设置为 `PendingReview` 审核状态
- 必须关联到已有的上传记录（`source_upload_id`）
- 至少包含一个图片区域（`source_region`）

## 关键验收条件摘要

- 提供有效的学生ID、学科、年级、上传记录ID和图片区域时，成功创建错题条目
- 创建后自动记录 ProcessLog（Action=Created）
- 验证失败时返回 `InvalidArgument` 错误

## 范围外

- 自动从图片识别错题区域
- 自动审核流程
- 批量添加错题

## 文档索引

| 文档 | 说明 |
|------|------|
| [02-SPEC.md](./02-SPEC.md) | 功能规格与验收标准 |
| [03-DESIGN.md](./03-DESIGN.md) | 设计与数据流 |
| [04-TASKS.md](./04-TASKS.md) | 任务清单 |
| [05-TESTS.md](./05-TESTS.md) | 测试文档 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 约定与规范 |
