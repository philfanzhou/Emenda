# AnalyzeMistakeType — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题类型分析通过 gRPC 接口接收错题条目 ID 及可选的内容/正确答案/学生答案，在领域服务层完成类型分析，返回错误分类结果（类型编号、类型名称、分析原因）。当前为硬编码占位实现，始终返回 type=5（"其他错误"），并记录处理日志。

**用户故事 1（教师分析错题）**：作为教师，我要对一条错题条目调用 AnalyzeMistakeType，传入错题 ID；系统分析后返回错误类型分类，同时记录一条 Analyzed 类型的处理日志。

## 功能要求清单（可独立测试）

- [x] FR-01 能根据合法的 `mistake_item_id` 调用分析接口，返回 `MistakeTypeResponse`（含 `type`、`type_name`、`analysis_reason`）。
- [x] FR-02 分析返回结果中 `type` 为整型错误分类编号，`type_name` 为对应的中文类型名称。
- [x] FR-03 当错题条目存在时，分析过程记录一条 `ProcessAction.Analyzed` 日志，details 包含分析出的类型名称。
- [x] FR-04 当错题条目不存在时，仅返回分析结果，不记录处理日志。
- [x] FR-05 `mistake_item_id` 必须为合法 GUID 格式，非法格式应在 gRPC 层被 `Guid.Parse` 抛出异常。

## 详细的验收标准（可自动验证）

- AC-FR-01：传入合法 `mistake_item_id` 后，返回的 `MistakeTypeResponse` 中 `Type == 5`、`TypeName == "其他错误"`、`AnalysisReason == "根据答案对比无法明确分类"`。
- AC-FR-02：传入合法 `mistake_item_id` 且错题条目存在时，`_processLogRepository.AddAsync` 被调用一次，传入的 `ProcessLogModel.Action == (int)ProcessAction.Analyzed`，`Details` 包含 "Mistake analyzed as 其他错误"。
- AC-FR-03：传入合法 `mistake_item_id` 且错题条目存在时，`_unitOfWork.SaveChangesAsync` 被调用一次。
- AC-FR-04：传入不存在的 `mistake_item_id` 时，`_processLogRepository.AddAsync` 不被调用，但仍返回有效的 `MistakeTypeResponse`。
- AC-FR-05：gRPC 层传入非法 GUID 字符串作为 `MistakeItemId` 时，`Guid.Parse` 抛出 `FormatException`。

## 非功能需求

- **性能**：分析操作 P95 < 500ms（当前为硬编码返回，实际 < 50ms）。
- **安全**：仅教师/管理员可调用此接口（由上层 API 网关负责鉴权）。
- **可靠性**：分析失败不影响其他模块；当错题不存在时静默跳过日志记录。
- **可观测性**：当前领域服务不记录 ILogger 日志；未来实装 AI 分析时应记录 Info 级别分析结果。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.AnalyzeMistakeTypeAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：无效 GUID、错题条目不存在。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储、工作单元）均可替换。
- **禁止事项**：不得写入真实生产数据库；不得依赖 AI 服务提供精确分析结果。