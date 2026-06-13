# 记录错题操作日志 (LogMistakeProcess)

## 核心用户故事

作为系统，我希望能够手动记录错题的操作日志，以便追踪错题生命周期中的自定义事件和操作详情。

## 补充约束

- 错题必须存在，不存在时返回 `success=false`
- 操作类型通过 `ProcessAction` 枚举指定
- 日志创建后不可修改和删除（仅通过删除错题级联删除）

## 关键验收条件摘要

- 错题存在时成功创建日志并返回 `success=true`
- 错题不存在时返回 `success=false`
- 日志自动设置 ID 和创建时间

## 范围外

- 修改或删除已有日志
- 日志查询（通过 GetMistakeProcessHistory 单独处理）
- 日志的自动触发（由其他操作自动记录）

## 文档索引

| 文档 | 说明 |
|------|------|
| [02-SPEC.md](./02-SPEC.md) | 功能规格与验收标准 |
| [03-DESIGN.md](./03-DESIGN.md) | 设计与数据流 |
| [04-TASKS.md](./04-TASKS.md) | 任务清单 |
| [05-TESTS.md](./05-TESTS.md) | 测试文档 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 约定与规范 |
