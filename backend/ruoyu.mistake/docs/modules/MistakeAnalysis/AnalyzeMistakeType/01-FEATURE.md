# 错题类型分析 (AnalyzeMistakeType)

## 核心用户故事

作为教师，我希望系统能自动分析错题的错误类型（如计算错误、概念错误等），以便针对性地制定教学策略。

## 补充约束

- 当前实现为**硬编码占位**：始终返回 type=5, typeName="其他错误", analysisReason="根据答案对比无法明确分类"
- Proto 定义了 content/correct_answer/student_answer 参数，但 Domain Service 未使用
- 若错题存在，会记录 ProcessLog（Action=Analyzed）
- 真正的错题类型分析逻辑**待实现**

## 关键验收条件摘要

1. 传入有效 mistakeItemId，返回 MistakeTypeResponse（当前为硬编码值）
2. 错题存在时，记录 ProcessLog（Action=Analyzed, details="Mistake analyzed as 其他错误"）
3. 错题不存在时，仍返回硬编码值但不记录 ProcessLog
4. Proto 中的 content/correct_answer/student_answer 字段当前未被使用

## 范围外

- 真正的错题类型分析算法（标记为 todo）
- 使用 content/correct_answer/student_answer 进行分析
- 多种错误类型的分类体系定义

## 文档索引

| 编号 | 文档 | 说明 |
|------|------|------|
| 01 | FEATURE.md | 功能概述 |
| 02 | SPEC.md | 详细规格 |
| 03 | DESIGN.md | 设计文档 |
| 04 | TASKS.md | 任务清单 |
| 05 | TESTS.md | 测试文档 |
| 06 | CONVENTIONS.md | 约定 |
