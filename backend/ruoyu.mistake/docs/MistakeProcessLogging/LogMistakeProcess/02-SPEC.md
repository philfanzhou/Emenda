# LogMistakeProcess — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题处理日志记录通过 gRPC 接口接收错题条目 ID、学生 ID、操作动作类型和详情描述，在领域服务层验证错题条目存在后将日志持久化到 PostgreSQL，通过 `IUnitOfWork.SaveChangesAsync` 提交事务。

**用户故事 1（教师记录操作）**：作为教师，我要为某条错题记录一个操作动作（如审核确认），系统验证错题存在后创建日志记录并返回成功。

## 功能要求清单（可独立测试）

- [x] FR-01 能根据合法的参数创建一条处理日志记录，写入 `process_log` 表。
- [x] FR-02 日志记录包含自动生成的 GUID 作为 `Id`。
- [x] FR-03 日志记录的 `MistakeItemId` 与入参 `mistake_item_id` 一致。
- [x] FR-04 日志记录的 `Action` 与入参 `action` 的整数值一致。
- [x] FR-05 日志记录的 `Details` 与入参 `details` 一致。
- [x] FR-06 日志记录的 `CreatedAt` 接近当前时间（误差 < 5s）。
- [x] FR-07 当 `mistake_item_id` 对应的错题条目不存在时，返回 `BoolResponse { Success = false }`。
- [x] FR-08 `mistake_item_id` 必须为合法 GUID 格式。

## 详细的验收标准（可自动验证）

- AC-FR-01：传入合法参数（存在的错题条目 ID、有效 action、非空 details）后，返回 `BoolResponse { Success = true }`。
- AC-FR-02：成功创建后，`_processLogRepository.AddAsync` 被调用一次，传入的 `ProcessLogModel.Id != Guid.Empty`、`MistakeItemId` 与入参一致、`Action` 与入参一致、`Details` 与入参一致、`CreatedAt` 接近当前时间（±5s）。
- AC-FR-03：成功创建后，`_unitOfWork.SaveChangesAsync` 被调用一次。
- AC-FR-04：传入不存在的 `mistake_item_id` 时，返回 `BoolResponse { Success = false }`，`_processLogRepository.AddAsync` 不被调用。
- AC-FR-05：gRPC 层传入非法 GUID 字符串作为 `MistakeItemId` 时，`Guid.Parse` 抛出 `FormatException`。

## 非功能需求

- **性能**：记录操作 P95 < 100ms。
- **安全**：仅教师/管理员可记录操作日志（由上层 API 网关负责鉴权）。
- **可靠性**：记录失败时事务回滚，不产生孤立数据。
- **可观测性**：关键错误（仓储写入失败）应通过 `ILogger` 记录 Error 级别日志。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.LogMistakeProcessAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：错题条目不存在、无效 GUID。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储、工作单元）均可替换。
- **禁止事项**：不得写入真实生产数据库。