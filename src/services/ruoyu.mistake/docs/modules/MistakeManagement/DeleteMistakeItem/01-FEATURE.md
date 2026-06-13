# 删除错题条目 (DeleteMistakeItem)

## 核心用户故事

作为教师，我希望能够删除不再需要的错题条目，以便清理无效或误录入的数据并释放关联资源。

## 补充约束

- 删除操作为级联删除：先删 practices，再删 process_logs，最后删 mistake_item
- 虽然数据库配置了 CASCADE 删除，代码层仍显式删除子表数据
- 错题不存在时返回 `success=false`（非错误）

## 关键验收条件摘要

- 删除时按 practices → process_logs → mistake_item 顺序执行
- 错题不存在时返回 `BoolResponse { success = false }`
- 删除成功时返回 `BoolResponse { success = true }`

## 范围外

- 软删除（逻辑删除）
- 删除前确认提示
- 批量删除
- 删除关联的 OSS 图片文件

## 文档索引

| 文档 | 说明 |
|------|------|
| [02-SPEC.md](./02-SPEC.md) | 功能规格与验收标准 |
| [03-DESIGN.md](./03-DESIGN.md) | 设计与数据流 |
| [04-TASKS.md](./04-TASKS.md) | 任务清单 |
| [05-TESTS.md](./05-TESTS.md) | 测试文档 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 约定与规范 |
