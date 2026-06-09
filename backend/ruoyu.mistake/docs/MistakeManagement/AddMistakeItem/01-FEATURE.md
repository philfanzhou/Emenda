# 错题条目手动添加 (AddMistakeItem)

## 功能名称和一句话概括

错题条目手动添加 — 教师通过 gRPC 接口手动创建错题条目，指定学生、学科、年级、来源图片区域与标注框，系统自动分配唯一 ID 并将审核状态设为待审核。

## 核心用户故事

1. **教师手动添加错题条目**：作为教师，我希望能够手动创建一条错题条目，输入学生 ID、学科、年级、来源上传 ID 以及图片区域（含标注框），系统自动生成唯一 ID 并设置为待审核状态，让我能快速录入错题信息。

2. **错题审核流程入口**：作为审核人员，新录入的错题条目默认标记为"待审核"，后续可通过审核流程进行确认或驳回。

## 关键验收条件摘要

- AC-1：创建错题条目时，student_id 不能为空，subject 必须在 0-9 范围内，grade 必须在 0-12 范围内，source_upload_id 不能为空，source_regions 不能为空。
- AC-2：创建成功后返回包含唯一 GUID 的完整 MistakeItemDto，ReviewStatus 为 PendingReview，CreatedAt 接近当前时间。
- AC-3：创建成功后自动记录一条 ProcessAction.Created 操作日志，内容为 "Mistake item manually added by teacher"。
- AC-4：SourceRegion 中的 BoundingBox 可以为 null（表示整张图片），也可包含 x1,y1,x2,y2 四个坐标值。

## 明确列出"范围外"（不做什么）

- 不处理错题审核（由 ReviewMistakeItem 模块负责）。
- 不验证 student_id 是否在 ruoyu.student 服务中真实存在。
- 不验证 source_upload_id 对应的上传记录是否存在。
- 不处理错题的更新、删除、查询（分别由 UpdateMistakeItem、DeleteMistakeItem、GetMistakeItem 模块负责）。
- 不实现批量导入。

## 文档索引

- [SPEC.md](./SPEC.md) — 详细需求与验收标准清单
- [DESIGN.md](./DESIGN.md) — 架构设计、接口签名与数据流
- [TASKS.md](./TASKS.md) — 开发/验证任务列表
- [TESTS.md](./TESTS.md) — 单元测试与集成测试计划
- [CONVENTIONS.md](./CONVENTIONS.md) — 命名约定、日志安全与代码风格