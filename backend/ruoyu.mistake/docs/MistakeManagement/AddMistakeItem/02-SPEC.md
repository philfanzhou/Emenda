# AddMistakeItem — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题条目手动添加是错题管理的基础录入操作，通过 gRPC 接口接收创建请求，在领域服务层完成模型构建与校验，通过仓储接口持久化到 PostgreSQL，并自动记录操作日志。

**用户故事 1（教师手动添加）**：作为教师，我要手动创建一条错题条目，输入学生 ID、学科、年级、来源上传 ID、图片区域列表以及可选备注；系统生成唯一 ID、设置审核状态为待审核、记录操作日志，并返回完整的错题条目信息。

## 功能要求清单（可独立测试）

- [x] FR-01 能创建错题条目，生成唯一 `Guid`，写入 `mistake_item` 表。
- [x] FR-02 student_id 不能为空（null/whitespace），否则返回 InvalidArgument 错误。
- [x] FR-03 subject 必须在 0-9 范围内，否则返回 InvalidArgument 错误。
- [x] FR-04 grade 必须在 0-12 范围内，否则返回 InvalidArgument 错误。
- [x] FR-05 source_upload_id 不能为空，否则返回 InvalidArgument 错误。
- [x] FR-06 source_regions 不能为空列表，否则返回 InvalidArgument 错误。
- [x] FR-07 创建成功后 ReviewStatus 自动设为 PendingReview。
- [x] FR-08 创建成功后自动记录一条 ProcessLog，Action=Created，Details="Mistake item manually added by teacher"。
- [x] FR-09 创建成功后返回包含 ID、student_id、subject、grade、source_upload_id、source_regions、review_status、created_at 的完整 MistakeItemDto。
- [x] FR-10 SourceRegion 的 BoundingBox 可以为 null，也可包含有效的 x1,y1,x2,y2 坐标。
- [x] FR-11 创建操作在同一事务中提交（错题条目 + 操作日志）。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `AddMistakeItemAsync` 后，数据库中应插入一条记录，`Id != Guid.Empty`，`StudentId` 与入参一致，`ReviewStatus == (int)ReviewStatus.PendingReview`，`CreatedAt` 接近当前时间（误差 < 5s）。
- AC-FR-02：通过 gRPC 传入空 student_id 时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-03：通过 gRPC 传入 subject < 0 或 subject > 9 时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-04：通过 gRPC 传入 grade < 0 或 grade > 12 时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-05：通过 gRPC 传入空 source_upload_id 时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-06：通过 gRPC 传入空 source_regions 列表时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-07：创建成功后 gRPC 返回的 `MistakeItemDto` 应包含正确的 `id`、`student_id`、`subject`、`grade`、`source_upload_id`、`source_regions`、`review_status`、`created_at` 字段，review_status 为 REVIEW_STATUS_PENDING_REVIEW。
- AC-FR-08：创建成功后，`process_log` 表应插入一条 Action=PROCESS_ACTION_CREATED 的记录，Details 包含 "Mistake item manually added by teacher"。
- AC-FR-09：SourceRegion 中 BoundingBox=null 时，创建应正常完成，SourceRegion.BoundingBox 为 null。
- AC-FR-10：SourceRegion 中包含有效 BoundingBox（如 x1=10,y1=20,x2=100,y2=200）时，返回的 DTO 中对应的 BoundingBox 坐标应与输入一致。
- AC-FR-11：事务提交失败（如 SaveChangesAsync 抛异常）时，错题条目录入和操作日志均不应持久化。

## 非功能需求

- **性能**：创建操作 P95 < 200ms。
- **安全**：仅教师/管理员可手动添加错题条目（由上层 API 网关负责鉴权）。
- **可靠性**：使用 `IUnitOfWork.SaveChangesAsync` 事务保护，确保错题条目与操作日志的原子性写入。
- **可观测性**：关键错误（事务失败）应通过 `ILogger` 记录 Error 级别日志。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.AddMistakeItemAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：空 student_id、无效 subject、无效 grade、空 source_upload_id、空 source_regions、事务失败。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储、工作单元）均可替换。
- **禁止事项**：不得写入真实生产数据库；不得依赖时间戳做精确相等断言（应使用时间范围）。