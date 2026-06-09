# 错题提交上传 (SubmitMistakeUpload)

## 功能名称和一句话概括

错题提交上传 — 学生或管理员上传错题图片创建错题条目，系统自动生成唯一 ID、校验参数并记录创建操作日志，基于 gRPC 暴露提交接口，使用 PostgreSQL 持久化与 EF Core 事务保护。

## 核心用户故事

1. **学生上传错题**：作为学生，我希望通过上传错题图片、指定学科和年级来创建一条待审核的错题条目，系统自动记录操作日志并返回创建的条目 ID，让我能快速完成错题归档。

2. **管理员批量处理**：作为管理员，当收到学生上传的错题条目后，系统将其标记为"待审核"状态，管理员可在后续流程中逐一审核确认。

## 关键验收条件摘要

- AC-1：提交时 student_id 不能为空，subject 必须在 1-9 范围内，grade 必须在 1-12 范围内，source_upload_id 不能为空，至少提供一张图片路径。
- AC-2：创建成功后返回 `success=true` 及 `created_item_ids` 列表，错题条目状态为 `PendingReview`。
- AC-3：每次提交会记录一条 `ProcessAction.Created` 操作日志，与错题条目在同一事务中写入。
- AC-4：参数校验失败时抛出 `RpcException(StatusCode.InvalidArgument)`，领域层通过 `MistakeValidationException` 收集所有校验错误。

## 明确列出"范围外"（不做什么）

- 不处理图片的实际上传与存储（由 ImageUpload 模块负责）。
- 不处理错题的审核、修改、删除（分别由 ReviewMistakeItem、UpdateMistakeItem、DeleteMistakeItem 负责）。
- 不验证 student_id 或 source_upload_id 是否在外部服务中真实存在。
- 不实现错题的 AI 自动分类与分析（由 AnalyzeMistakeType 负责）。
- 不处理同一上传记录的幂等合并（每次调用均创建新条目）。

## 文档索引

- [SPEC.md](./SPEC.md) — 详细需求与验收标准清单
- [DESIGN.md](./DESIGN.md) — 架构设计、接口签名与数据流
- [TASKS.md](./TASKS.md) — 开发/验证任务列表
- [TESTS.md](./TESTS.md) — 单元测试与集成测试计划
- [CONVENTIONS.md](./CONVENTIONS.md) — 命名约定、日志安全与代码风格