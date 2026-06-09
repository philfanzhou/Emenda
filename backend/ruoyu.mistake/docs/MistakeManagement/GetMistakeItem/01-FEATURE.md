# 错题条目查询 (GetMistakeItem / GetMistakeItemsByUpload / GetMistakeItemList)

## 功能名称和一句话概括

错题条目查询 — 通过 gRPC 接口提供三种查询方式：按 ID 获取单条错题、按上传记录 ID 获取错题列表、支持多条件过滤的分页查询，满足教师和系统的不同检索需求。

## 核心用户故事

1. **教师查看单条错题详情**：作为教师，我可以通过错题条目 ID 查询单条错题的完整信息，包括学生、学科、年级、来源图片区域、审核状态等。

2. **教师按上传批次查看错题**：作为教师，在审核某次上传的错题时，我需要查看该次上传产生的所有错题条目，系统按 source_upload_id 返回完整列表。

3. **教师分页浏览错题列表**：作为教师，我可以通过学生 ID、学科、年级、审核状态等条件进行分页查询，按需浏览错题条目。

## 关键验收条件摘要

- AC-1：单条查询（GetMistakeItem）条目不存在时返回 NotFound 错误。
- AC-2：按上传记录查询（GetMistakeItemsByUpload）返回该 source_upload_id 关联的所有错题条目。
- AC-3：分页查询（GetMistakeItemList）默认 page=1、size=10，最大 size=100；返回 items 和 page_meta（含 total_count、total_pages）。
- AC-4：分页查询支持按 student_id、subject、grade、review_status 过滤。
- AC-5：返回的 MistakeItemDto 包含完整的 source_regions（含 BoundingBox 坐标）和时间字段。

## 明确列出"范围外"（不做什么）

- 不实现全文搜索或模糊匹配（仅支持精确过滤）。
- 不实现排序自定义（使用数据库默认排序）。
- 不实现跨上传记录的合并查询。
- 不处理错题的创建、更新、删除（分别由 AddMistakeItem、UpdateMistakeItem、DeleteMistakeItem 模块负责）。
- 不实现缓存策略（每次查询直接访问数据库）。

## 文档索引

- [SPEC.md](./SPEC.md) — 详细需求与验收标准清单
- [DESIGN.md](./DESIGN.md) — 架构设计、接口签名与数据流
- [TASKS.md](./TASKS.md) — 开发/验证任务列表
- [TESTS.md](./TESTS.md) — 单元测试与集成测试计划
- [CONVENTIONS.md](./CONVENTIONS.md) — 命名约定、日志安全与代码风格