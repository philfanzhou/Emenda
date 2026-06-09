# 待审核上传列表查询 (GetPendingReviewUploads)

## 功能名称和一句话概括

待审核上传列表查询 — 分页查询哪些上传记录中还有待审核的错题条目，支持按学科筛选，返回每个上传记录的待审核数量统计。

## 核心用户故事

1. **老师查看待审核上传**：作为老师，我希望查看所有包含待审核错题的上传记录列表，按学科筛选、分页浏览，快速定位需要审核的上传批次。

2. **审核工作分配**：作为审核管理员，我需要了解每个上传记录的待审核数量（pending_count）和总错题数（total_count），以便合理分配审核工作量。

## 关键验收条件摘要

- AC-1：返回仅包含有待审核错题（`ReviewStatus == PendingReview`）的上传记录列表。
- AC-2：每条记录包含 `source_upload_id`、`student_id`、`subject`、`grade`、`pending_count`、`total_count`、`earliest_created_at` 字段。
- AC-3：支持分页查询（page/size），返回 PageMeta 分页元信息。
- AC-4：支持按学科列表（subjects）筛选，不传 subjects 时不过滤学科。
- AC-5：分页参数 `size` 上限 100，`page` 至少为 1。

## 明确列出"范围外"（不做什么）

- 不返回单个错题的明细信息（由 `GetMistakeItemList` / `GetMistakeItemsByUpload` 提供）。
- 不提供排序选项（目前按 earliest_created_at 自然排序）。
- 不处理审核完成的记录（已完成审核的上传记录不出现在此列表中）。
- 不支持全文搜索或按学生 ID 筛选。

## 文档索引

- [SPEC.md](./SPEC.md) — 详细需求与验收标准清单
- [DESIGN.md](./DESIGN.md) — 架构设计、接口签名与数据流
- [TASKS.md](./TASKS.md) — 开发/验证任务列表
- [TESTS.md](./TESTS.md) — 单元测试与集成测试计划
- [CONVENTIONS.md](./CONVENTIONS.md) — 命名约定、日志安全与代码风格