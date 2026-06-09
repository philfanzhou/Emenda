# SubmitMistakeUpload — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题提交上传是错题管理的入口操作，通过 gRPC 接口接收包含学生 ID、学科、年级、图片路径和上传记录 ID 的提交请求，在领域服务层完成参数校验、模型构建与状态初始化，通过仓储接口持久化到 PostgreSQL，并在同一事务中写入操作日志。

**用户故事 1（学生上传错题）**：作为学生，我要提交错题图片，指定学科和年级；系统校验所有必填参数、创建待审核错题条目、记录操作日志，并返回创建的条目 ID 列表。

## 功能要求清单（可独立测试）

- [x] FR-01 能创建错题条目，生成唯一 `Guid`，写入 `mistake_item` 表。
- [x] FR-02 每条图片路径转换为 `SourceRegionModel`，`BoundingBox` 为 null。
- [x] FR-03 创建时 `ReviewStatus` 自动设为 `PendingReview`（值 1）。
- [x] FR-04 student_id 不能为空或空白，否则返回 InvalidArgument 错误。
- [x] FR-05 subject 必须在 1-9 范围内，否则返回 InvalidArgument 错误。
- [x] FR-06 grade 必须在 1-12 范围内，否则返回 InvalidArgument 错误。
- [x] FR-07 source_upload_id 不能为空或空白，否则返回 InvalidArgument 错误。
- [x] FR-08 image_paths 至少包含一条路径，否则返回 InvalidArgument 错误。
- [x] FR-09 创建成功后记录 `ProcessAction.Created` 操作日志。
- [x] FR-10 错题条目与操作日志在同一事务中写入，确保数据一致性。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `SubmitMistakeUploadAsync` 后，数据库中应插入一条 `mistake_item` 记录，`Id != Guid.Empty`，`StudentId` 与入参一致，`CreatedAt` 接近当前时间（误差 < 5s）。
- AC-FR-02：创建的 `SourceRegions` 数量与传入的 `imagePaths` 数量一致，每条 `SourceImagePath` 与入参一一对应，`BoundingBox` 为 null。
- AC-FR-03：创建后的 `ReviewStatus` 等于 `(int)ReviewStatus.PendingReview`。
- AC-FR-04：通过 gRPC 传入空 student_id 或纯空格时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-05：通过 gRPC 传入 subject=0 或 subject>9 时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-06：通过 gRPC 传入 grade=0 或 grade>12 时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-07：通过 gRPC 传入空 source_upload_id 时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-08：通过 gRPC 传入空 image_paths 列表时，应抛出 `RpcException(StatusCode.InvalidArgument)`。
- AC-FR-09：创建成功后 `_processLogRepository.AddAsync` 被调用一次，`Action == (int)ProcessAction.Created`。
- AC-FR-10：若事务中日志写入失败，错题条目也应回滚，数据库中不存在任何新记录。

## 非功能需求

- **性能**：提交操作 P95 < 200ms。
- **安全**：由上层 API 网关负责鉴权；student_id 不应在日志中以明文完整记录。
- **可靠性**：使用 `IUnitOfWork.SaveChangesAsync` 事务保护，确保错题条目与操作日志的原子性写入。
- **可观测性**：关键错误（事务失败）应通过 `ILogger` 记录 Error 级别日志。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.SubmitMistakeUploadAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：空 student_id、无效 subject、无效 grade、空 image_paths、空 source_upload_id、事务失败。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储、工作单元）均可替换。
- **禁止事项**：不得写入真实生产数据库；不得依赖时间戳做精确相等断言（应使用时间范围）。