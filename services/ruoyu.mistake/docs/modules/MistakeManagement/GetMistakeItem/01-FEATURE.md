# 错题条目查询 (GetMistakeItem)

## 一句话概括

根据错题 ID 查询单条错题条目的详细信息。

## 核心用户故事

1. 作为教师，我希望通过错题 ID 查看某条错题的详细信息，以便了解该错题的审核状态、图片区域和关联数据

## 关键验收条件摘要

- [ ] AC-01: 传入有效 ID 返回对应 MistakeItemDto
- [ ] AC-02: 传入不存在的 ID 返回 gRPC NotFound 错误

## 明确范围外

- 不支持批量查询（由 GetMistakeItemList 负责）
- 不返回关联的练习记录或过程日志

## 文档索引

- [02-SPEC - 详细需求规格](./02-SPEC.md)
- [03-DESIGN - 设计说明](./03-DESIGN.md)
- [04-TASKS - 任务清单](./04-TASKS.md)
- [05-TESTS - 测试计划](./05-TESTS.md)
- [06-CONVENTIONS - 约定规范](./06-CONVENTIONS.md)
