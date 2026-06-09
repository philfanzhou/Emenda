# 错题处理日志记录 (LogMistakeProcess)

## 功能名称和一句话概括

错题处理日志记录 — 为指定错题条目记录一条处理动作日志（如创建、分析、审核确认、驳回、练习、掌握），通过 gRPC 接收动作类型与详情描述，持久化到 PostgreSQL。

## 核心用户故事

1. **教师追踪错题处理过程**：作为教师，每当对错题执行一项操作（审核确认、驳回等），我希望系统自动记录该操作的日志，包含操作类型、操作描述和时间戳，以便后续追踪错题的处理历史。

2. **管理员审计错题管理流程**：作为管理员，我希望通过处理日志了解每条错题的完整生命周期，包括何时创建、何时分析、何时审核、何时被练习掌握，确保错题管理流程的合规性。

## 关键验收条件摘要

- AC-1：传入合法的 `mistake_item_id`、`student_id`、`action`（ProcessAction 枚举有效值）、`details` 后，返回 `BoolResponse { Success = true }`。
- AC-2：当 `mistake_item_id` 对应的错题条目不存在时，返回 `BoolResponse { Success = false }`，不抛出异常。
- AC-3：创建的日志记录包含自动生成的 GUID、正确的 `MistakeItemId`、对应的 `Action` 值、`Details` 描述和接近当前时间的 `CreatedAt`。
- AC-4：`action` 必须为 ProcessAction 枚举的有效值（不包括 UNSPECIFIED），但当前实现由领域服务接收后不做校验。

## 明确列出"范围外"（不做什么）

- 不校验 `action` 枚举值是否为 UNSPECIFIED（无操作）。
- 不校验 `student_id` 是否有效或在 ruoyu.student 服务中存在。
- 不实现日志的分页查询（由 GetMistakeProcessHistory 负责）。
- 不实现日志的修改或删除。
- 不实现基于日志触发的自动化工作流。

## 文档索引

- [SPEC.md](./SPEC.md) — 详细需求与验收标准清单
- [DESIGN.md](./DESIGN.md) — 架构设计、接口签名与数据流
- [TASKS.md](./TASKS.md) — 开发/验证任务列表
- [TESTS.md](./TESTS.md) — 单元测试与集成测试计划
- [CONVENTIONS.md](./CONVENTIONS.md) — 命名约定、日志安全与代码风格