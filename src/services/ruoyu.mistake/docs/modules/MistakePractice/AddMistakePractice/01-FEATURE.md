# 添加错题练习 (AddMistakePractice)

## 核心用户故事

作为学生，我希望能够对已确认的错题进行练习并记录结果，以便追踪我的错题掌握进度。

## 补充约束

- 只能对审核状态为 `Confirmed` 的错题进行练习
- 错题不存在或未确认时抛出 `MistakeValidationException`
- 练习记录自动设置 ID 和练习时间
- 可选附带图片路径

## 关键验收条件摘要

- 错题必须存在且 ReviewStatus=Confirmed 才能添加练习
- 自动记录 ProcessLog（Action=Practiced）
- 可选 imagePaths 参数

## 范围外

- 对未确认/待审核的错题进行练习
- 自动评判答案正确性
- 练习结果统计

## 文档索引

| 文档 | 说明 |
|------|------|
| [02-SPEC.md](./02-SPEC.md) | 功能规格与验收标准 |
| [03-DESIGN.md](./03-DESIGN.md) | 设计与数据流 |
| [04-TASKS.md](./04-TASKS.md) | 任务清单 |
| [05-TESTS.md](./05-TESTS.md) | 测试文档 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 约定与规范 |
