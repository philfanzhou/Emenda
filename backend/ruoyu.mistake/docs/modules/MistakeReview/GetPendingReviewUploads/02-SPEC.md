# GetPendingReviewUploads - 功能规格

## 功能概述

教师查看待审核的错题上传列表，按上传批次（source_upload_id）分组聚合，支持分页和学科筛选。

## 用户故事

作为教师，我希望查看待审核的错题上传列表（按上传批次分组），以便高效地逐批审核学生提交的错题。

## 功能要求

- [x] FR-01: 接收分页参数（page、size）和可选学科筛选（subjects）
- [x] FR-02: 分页参数修正：page<=0→1，size<=0→10，size>100→100
- [x] FR-03: 筛选 ReviewStatus=PendingReview 的错题条目
- [x] FR-04: 可选按 subjects 列表筛选
- [x] FR-05: 按 source_upload_id 分组，返回 PendingReviewUploadModel
- [x] FR-06: 按 EarliestCreatedAt 升序排列
- [x] FR-07: 返回分页元数据（page、size、total_count、total_pages）
- [x] FR-08: EarliestCreatedAt 格式化为 ISO 8601 UTC 字符串

## 验收标准

### AC-FR-01: 基本查询

- **Given** 存在 PendingReview 状态的错题条目
- **When** 调用 GetPendingReviewUploads(page=1, size=10)
- **Then** 返回按 source_upload_id 分组的上传列表和总数

### AC-FR-02: 分页修正

- **Given** page=0 或 page=-1
- **When** 调用 GetPendingReviewUploadsAsync
- **Then** page 被修正为 1

- **Given** size=0 或 size=-1
- **When** 调用 GetPendingReviewUploadsAsync
- **Then** size 被修正为 10

- **Given** size=200
- **When** 调用 GetPendingReviewUploadsAsync
- **Then** size 被修正为 100

### AC-FR-03: PendingReview 筛选

- **Given** 存在 Confirmed 和 PendingReview 状态的错题条目
- **When** 调用 GetPendingReviewUploads
- **Then** 仅返回 PendingReview 状态的条目分组

### AC-FR-04: subjects 筛选

- **Given** subjects 列表为 [1, 3]
- **When** 调用 GetPendingReviewUploads
- **Then** 仅返回 subject 为 1 或 3 的待审核条目分组

- **Given** subjects 列表为空
- **When** 调用 GetPendingReviewUploads
- **Then** 不按学科筛选，返回所有待审核条目分组

### AC-FR-05: 分组聚合

- **Given** 同一 source_upload_id 下有 3 条 PendingReview 错题
- **When** 查询结果
- **Then** 该上传批次在结果中出现一次，PendingCount=3，TotalCount=3

### AC-FR-06: 排序

- **Given** 多个上传批次的 EarliestCreatedAt 分别为 T1、T2、T3（T1 < T2 < T3）
- **When** 查询结果
- **Then** 按 EarliestCreatedAt 升序排列

### AC-FR-07: 分页元数据

- **Given** 共 25 个上传批次，page=2, size=10
- **When** 查询结果
- **Then** PageMeta 包含 page=2, size=10, total_count=25, total_pages=3

### AC-FR-08: EarliestCreatedAt 格式化

- **Given** EarliestCreatedAt 为有效 DateTimeOffset
- **When** 映射到 PendingReviewUploadDto
- **Then** EarliestCreatedAt 格式化为 "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"

- **Given** EarliestCreatedAt 为 DateTimeOffset.MinValue
- **When** 映射到 PendingReviewUploadDto
- **Then** EarliestCreatedAt 为空字符串

## 非功能需求

- NFR-01: 因 SQLite 不支持 DateTimeOffset 的 Min 聚合函数，分组聚合在客户端执行
- NFR-02: 客户端聚合意味着数据量大时需考虑内存占用，size 上限为 100 可缓解此问题
- NFR-03: 分页在客户端聚合后执行，确保分页基于分组后的总数

## 测试策略

- 单元测试覆盖领域服务 `MistakeDomainService.GetPendingReviewUploadsAsync`
- 验证分页参数修正和基本查询
- 仓储层客户端分组聚合逻辑由集成测试覆盖
