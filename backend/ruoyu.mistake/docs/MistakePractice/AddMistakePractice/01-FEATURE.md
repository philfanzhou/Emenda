# 添加错题练习 (AddMistakePractice)

## 功能名称和一句话概括

添加错题练习 — 学生对已确认的错题条目进行一次练习，记录练习图片、作答答案和正确性判定，系统校验错题状态后在同一事务中创建练习记录和操作日志，基于 gRPC 暴露添加接口。

## 核心用户故事

1. **学生练习错题**：作为学生，我针对已确认的错题条目进行练习，上传练习图片、提交我的答案并标记是否正确；系统记录本次练习并更新操作日志，帮助我追踪练习进度。

2. **教师查看练习记录**：作为教师，我希望在错题详情页看到学生的所有练习记录和答题情况，以便评估学生对该错题的掌握程度。

## 关键验收条件摘要

- AC-1：只能对 `ReviewStatus == Confirmed` 的错题条目添加练习，否则返回 InvalidArgument 错误。
- AC-2：练习创建后自动分配唯一 Guid、设置 `PracticeDate` 为当前时间、记录 `StudentAnswer` 和 `IsCorrect`。
- AC-3：每次练习记录一条 `ProcessAction.Practiced` 操作日志，与练习记录在同一事务中写入。
- AC-4：`image_paths` 为可选字段，不传或传空列表时 `ImagePaths` 保持为空。

## 明确列出"范围外"（不做什么）

- 不处理练习图片的实际上传与存储（由 ImageUpload 模块负责）。
- 不处理练习记录的修改、删除（仅通过 DeleteMistakeItem 级联删除）。
- 不验证 student_id 是否与错题条目所属学生一致（由上层业务逻辑保证）。
- 不实现练习统计、进度追踪、掌握度判定（由 AnalyzeMistakeType 负责）。
- 不实现练习记录的查询（由 GetMistakePractices 负责）。

## 文档索引

- [SPEC.md](./SPEC.md) — 详细需求与验收标准清单
- [DESIGN.md](./DESIGN.md) — 架构设计、接口签名与数据流
- [TASKS.md](./TASKS.md) — 开发/验证任务列表
- [TESTS.md](./TESTS.md) — 单元测试与集成测试计划
- [CONVENTIONS.md](./CONVENTIONS.md) — 命名约定、日志安全与代码风格