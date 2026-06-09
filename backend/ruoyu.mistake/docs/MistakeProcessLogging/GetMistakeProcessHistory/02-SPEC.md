# GetMistakeProcessHistory — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题处理历史查询通过 gRPC 接口接收错题条目 ID，在领域服务层通过仓储查询该条目下的所有处理日志，按创建时间升序返回日志列表。

**用户故事 1（教师查看处理历史）**：作为教师，我要通过错题 ID 查询该错题的全部处理日志；系统返回按时间排序的日志列表，每条日志包含操作类型、详情和时间戳。

## 功能要求清单（可独立测试）

- [x] FR-01 能根据合法的错题条目 ID 查询对应的处理日志列表。
- [x] FR-02 返回的日志列表为 `ProcessLogDto` 数组，每个 DTO 包含 `id`、`mistake_item_id`、`action`、`details`、`created_at` 字段。
- [x] FR-03 日志按 `created_at` 升序排列。
- [x] FR-04 当错题条目 ID 无对应日志时，返回空列表。
- [x] FR-05 `id` 参数必须为合法 GUID 格式。

## 详细的验收标准（可自动验证）

- AC-FR-01：传入合法的错题条目 ID（对应已存在若干条日志）后，返回的 `ProcessLogListResponse.Logs` 包含与数据库中相同的日志条目数。
- AC-FR-02：返回的 `ProcessLogDto` 中 `Id`、`MistakeItemId`、`Action`、`Details`、`CreatedAt` 与仓储返回的 `ProcessLogModel` 一致。
- AC-FR-03：传入有日志的错题条目 ID 时，`_processLogRepository.GetByMistakeItemIdAsync` 被调用一次，传入的 ID 与入参一致。
- AC-FR-04：传入无对应日志的错题条目 ID 时，返回空的 `ProcessLogListResponse.Logs`。
- AC-FR-05：gRPC 层传入非法 GUID 字符串作为 `Id` 时，`Guid.Parse` 抛出 `FormatException`。

## 非功能需求

- **性能**：查询操作 P95 < 200ms。
- **安全**：仅教师/管理员可查询处理历史（由上层 API 网关负责鉴权）。
- **可靠性**：查询为只读操作，不修改任何数据。
- **可观测性**：不记录日志（纯查询操作）。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.GetMistakeProcessHistoryAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：无效 GUID。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储）均可替换。
- **禁止事项**：不得写入真实生产数据库。