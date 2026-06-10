# 获取待审核上传列表 (GetPendingReviewUploads)

## 核心用户故事

作为教师，我希望查看待审核的错题上传列表（按上传批次分组），以便高效地逐批审核学生提交的错题。

## 补充约束

- 分页修正：page<=0 → 1，size<=0 → 10，size>100 → 100
- 按 source_upload_id 分组聚合，每组返回一个 PendingReviewUploadModel
- 可选 subjects 筛选
- 因 SQLite 不支持 DateTimeOffset 的 Min 聚合，在客户端进行分组和聚合
- PendingCount 和 TotalCount 当前值相同（仅统计 PendingReview 状态的条目）

## 关键验收条件

- [x] 返回 PendingReview 状态的错题按 source_upload_id 分组
- [x] 分页参数修正（page/size 边界处理）
- [x] 可选 subjects 筛选
- [x] 按 EarliestCreatedAt 升序排列
- [x] 返回分页元数据（page/size/total_count/total_pages）
- [x] 客户端分组聚合（SQLite 兼容性）

## 范围外

- 单个错题条目的详细信息（由 GetMistakeItemsByUpload 提供）
- 审核操作（由 ReviewMistakeItem 提供）
- 按学生ID筛选
- 按时间范围筛选

## 文档索引

- [02-SPEC.md](./02-SPEC.md) - 功能规格与验收标准
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 约定与规范
