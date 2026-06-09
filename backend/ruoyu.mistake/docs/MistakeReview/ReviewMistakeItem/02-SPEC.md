# ReviewMistakeItem — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题审核是错题管理的核心操作之一，通过 gRPC 接口接收审核请求，在领域服务层完成状态校验与数据更新，通过仓储接口持久化到 PostgreSQL，并记录审核操作日志。

**用户故事 1（老师审核错题）**：作为老师，我要对指定错题条目进行审核，选择确认或驳回；系统校验错题状态为待审核后，更新审核状态、审核人、审核时间、审核备注，并记录操作日志。

## 功能要求清单（可独立测试）

- [x] FR-01 能通过 gRPC 接收审核请求，返回 BoolResponse 表示操作是否成功。
- [x] FR-02 审核结果 `review_status` 只能为 `Confirmed` 或 `Rejected`，否则在 gRPC 层返回 InvalidArgument 错误。
- [x] FR-03 错题当前状态必须为 `PendingReview`，否则返回验证错误。
- [x] FR-04 审核成功后，更新 `review_status`、`reviewer_id`、`reviewed_at`（当前时间）、`review_comment`、`updated_at`。
- [x] FR-05 可选择更新 `grade`：传入值在 1-12 范围内时更新，传入 ≤0 或 null 时不更新。
- [x] FR-06 可选择更新 `subject`：传入值在 1-9 范围内时更新，传入 ≤0 或 null 时不更新。
- [x] FR-07 审核操作记录操作日志：确认为 `ProcessAction.ReviewConfirmed`，驳回为 `ProcessAction.ReviewRejected`。
- [x] FR-08 错题不存在时返回 null（gRPC 层返回 `BoolResponse { Success = false }`）。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `ReviewMistakeItem` gRPC 方法传入有效参数，返回 `BoolResponse { Success = true }`。
- AC-FR-02：传入 `ReviewStatus.Unspecified` 或 `ReviewStatus.PendingReview` 作为审核结果，抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-03：对已确认或已驳回的错题调用审核，抛出 `MistakeValidationException`，消息为 "只能审核待审核状态的错题"。
- AC-FR-04：审核后数据库中错题记录的 `review_status == 2`（确认）或 `3`（驳回），`reviewer_id` 与入参一致，`reviewed_at` 接近当前时间（±5s），`review_comment` 与入参一致。
- AC-FR-05：传入 `grade = 5` 时，错题 `grade` 更新为 5；传入 `grade = 0` 或 null 时，`grade` 保持原值不变。
- AC-FR-06：传入 `subject = 3` 时，错题 `subject` 更新为 3；传入 `subject = 0` 或 null 时，`subject` 保持原值不变。
- AC-FR-07：审核后 `process_log` 表中新增一条记录，`action == 3`（确认）或 `4`（驳回），`mistake_item_id` 与错题 ID 一致。
- AC-FR-08：传入不存在的错题 ID，领域服务返回 null，gRPC 返回 `BoolResponse { Success = false }`。

## 非功能需求

- **性能**：审核操作 P95 < 100ms。
- **安全**：仅审核人可执行审核（由上层 API 网关负责鉴权）；操作应记录审核日志。
- **可靠性**：使用 `IUnitOfWork.SaveChangesAsync` 事务保护，确保错题状态更新与操作日志的原子性写入。
- **可观测性**：关键错误（错题不存在、状态转换非法）应通过 `ILogger` 记录 Warning 级别日志。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.ReviewMistakeItemAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：错题不存在、错题非待审核状态、审核结果值非法、grade 边界值、subject 边界值。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储、工作单元）均可替换。
- **禁止事项**：不得写入真实生产数据库；不得依赖时间戳做精确相等断言（应使用时间范围）。