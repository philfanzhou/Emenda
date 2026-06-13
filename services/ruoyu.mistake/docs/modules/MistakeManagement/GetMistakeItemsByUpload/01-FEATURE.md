# 按上传记录查询错题 (GetMistakeItemsByUpload)

## 一句话概括

根据上传记录 ID 查询该上传下的所有错题条目，按创建时间升序排列。

## 核心用户故事

1. 作为教师，我希望查看某次上传记录下的所有错题条目，以便了解该上传的整体审核情况

## 补充约束

- 无分页，返回全部错题条目
- 排序在客户端完成（SQLite 不支持 DateTimeOffset 的 ORDER BY）

## 关键验收条件摘要

- [ ] AC-01: 传入 sourceUploadId 返回该上传下的所有错题条目
- [ ] AC-02: 无错题时返回空列表
- [ ] AC-03: 结果按 created_at 升序排列（客户端排序）

## 明确范围外

- 不支持分页
- 不支持自定义排序
- 不返回上传记录本身的元信息

## 文档索引

- [02-SPEC - 详细需求规格](./02-SPEC.md)
- [03-DESIGN - 设计说明](./03-DESIGN.md)
- [04-TASKS - 任务清单](./04-TASKS.md)
- [05-TESTS - 测试计划](./05-TESTS.md)
- [06-CONVENTIONS - 约定规范](./06-CONVENTIONS.md)
