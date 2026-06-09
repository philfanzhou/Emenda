# GetPendingReviewUploads — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：待审核上传列表查询是审核工作台的核心数据接口，通过 gRPC 分页返回包含待审核错题的上传记录汇总，支持学科过滤，帮助审核人员快速了解待处理任务。

**用户故事 1（老师查看待审核列表）**：作为老师，我要打开审核工作台时看到所有待审核上传记录的分页列表，每条记录显示上传 ID、学生、学科、年级、待审核数量和总错题数，让我能选择要开始审核的上传批次。

## 功能要求清单（可独立测试）

- [x] FR-01 通过 gRPC 接收分页请求（page/size），返回 `PendingReviewUploadListResponse`。
- [x] FR-02 返回的每条 `PendingReviewUploadDto` 包含 `source_upload_id`、`student_id`、`subject`、`grade`、`pending_count`、`total_count`、`earliest_created_at` 字段。
- [x] FR-03 `pending_count` 为该上传记录下 `ReviewStatus == PendingReview` 的错题数量。
- [x] FR-04 `total_count` 为该上传记录下的错题总数（不限状态）。
- [x] FR-05 返回的 `PageMeta` 包含 `page`、`size`、`total_count`、`total_pages`。
- [x] FR-06 支持可选 `subjects` 筛选参数：传入学科 ID 列表时过滤结果；不传或空列表时不过滤。
- [x] FR-07 分页参数 `page` 至少为 1，`size` 至少为 1 且最大 100，超出范围自动修正。
- [x] FR-08 无待审核记录时返回空列表，total_count 为 0。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `GetPendingReviewUploads` gRPC 方法传入 `page=1, size=10`，返回 `PendingReviewUploadListResponse`，`page_meta.page == 1`，`page_meta.size == 10`。
- AC-FR-02：返回的每个 `PendingReviewUploadDto` 中 `source_upload_id` 非空、`student_id` 非空、`subject` 和 `grade` 有效、`pending_count >= 1`、`total_count >= pending_count`。
- AC-FR-03：`pending_count` 必须精确等于该上传记录下 `ReviewStatus == PendingReview` 的错题数量。
- AC-FR-04：`total_count` 必须精确等于该上传记录下所有错题的数量。
- AC-FR-05：`page_meta.total_pages` 等于 `ceil(total_count / size)`。
- AC-FR-06：传入 `subjects = [1, 2]` 时，仅返回学科为 1 或 2 的上传记录；不传 `subjects` 时返回所有学科。
- AC-FR-07：传入 `page = 0` 时自动修正为 `page = 1`；传入 `size = 200` 时自动修正为 `size = 100`。
- AC-FR-08：数据库中所有错题均已审核完成时，返回的 `uploads` 列表为空，`page_meta.total_count == 0`。

## 非功能需求

- **性能**：查询操作 P95 < 200ms（单次分页查询 ≤100 条）。
- **安全**：仅审核人员可查询待审核列表（由上层 API 网关负责鉴权）。
- **可靠性**：查询为只读操作，不修改数据，无事务要求。
- **可观测性**：查询失败时应通过 `ILogger` 记录 Error 级别日志，包含请求参数。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.GetPendingReviewUploadsAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：分页参数修正（page≤0、size≤0、size>100）、subjects 过滤正确性、空结果。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；仓储通过 Mock 返回预设数据。
- **禁止事项**：不得写入真实生产数据库；分页参数修正逻辑必须通过单元测试验证。