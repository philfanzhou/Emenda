# 错题列表查询 (GetMistakeItemList)

## 一句话概括

分页查询错题列表，支持按学生ID、学科、年级、审核状态筛选。

## 核心用户故事

1. 作为教师，我希望按学生ID和学科筛选错题列表，以便查看特定学生在特定学科的所有错题
2. 作为教师，我希望按审核状态筛选错题，以便快速找到待审核的错题
3. 作为前端，我希望获取分页的错题列表，以便在界面中展示

## 关键验收条件摘要

- [ ] AC-01: 传入 studentId/subject/grade/reviewStatus/page/size，返回分页结果
- [ ] AC-02: page <= 0 时自动修正为 1
- [ ] AC-03: size <= 0 时自动修正为 10
- [ ] AC-04: size > 100 时自动修正为 100
- [ ] AC-05: subject/grade/reviewStatus 为 0 时表示不筛选该项
- [ ] AC-06: 返回结果包含 items 列表、totalCount 总数、分页元信息

## 明确范围外

- 不处理复杂的多条件组合查询（如 OR 逻辑）
- 不返回脱敏数据（如不隐藏学生 ID）
- 不支持自定义排序（固定按 created_at 降序）

## 文档索引

- [02-SPEC - 详细需求规格](./02-SPEC.md)
- [03-DESIGN - 设计说明](./03-DESIGN.md)
- [04-TASKS - 任务清单](./04-TASKS.md)
- [05-TESTS - 测试计划](./05-TESTS.md)
- [06-CONVENTIONS - 约定规范](./06-CONVENTIONS.md)