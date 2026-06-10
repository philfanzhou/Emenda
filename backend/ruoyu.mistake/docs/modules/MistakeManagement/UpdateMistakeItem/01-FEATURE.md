# 更新错题条目 (UpdateMistakeItem)

## 核心用户故事

作为教师，我希望能够更新已有错题条目的信息，以便修正录入错误或补充错题详情。

## 补充约束

- 仅更新请求中非空/非零的字段（部分更新语义）
- 更新前必须确认错题条目存在
- 更新后需重新查询返回最新数据

## 关键验收条件摘要

- 错题不存在时返回 `NotFound` 错误
- 仅更新请求中提供的非空字段
- 更新成功后返回最新的 MistakeItemDto
- 更新失败时返回 `Internal` 错误

## 范围外

- 更新审核状态（通过 ReviewMistakeItem 单独处理）
- 更新 ProcessLog 记录
- 批量更新

## 文档索引

| 文档 | 说明 |
|------|------|
| [02-SPEC.md](./02-SPEC.md) | 功能规格与验收标准 |
| [03-DESIGN.md](./03-DESIGN.md) | 设计与数据流 |
| [04-TASKS.md](./04-TASKS.md) | 任务清单 |
| [05-TESTS.md](./05-TESTS.md) | 测试文档 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 约定与规范 |
