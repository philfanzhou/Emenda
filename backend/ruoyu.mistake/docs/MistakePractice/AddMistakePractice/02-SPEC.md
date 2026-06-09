# AddMistakePractice — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：添加错题练习是错题学习闭环的关键操作，通过 gRPC 接口接收练习请求（关联错题条目 ID、学生 ID、练习图片、答案和正确性），在领域服务层完成错题状态校验、练习模型构建和时间戳设置，通过仓储接口持久化到 PostgreSQL，并在同一事务中写入操作日志。

**用户故事 1（学生练习错题）**：作为学生，我要对已确认的错题进行练习，提交我的答案和是否正确的结果；系统校验错题状态、创建练习记录、记录练习日志，并返回完整的练习信息。

## 功能要求清单（可独立测试）

- [x] FR-01 能创建练习记录，生成唯一 `Guid`，写入 `mistake_practice` 表。
- [x] FR-02 练习创建前校验对应的 `mistake_item` 存在，否则抛出 `MistakeValidationException`。
- [x] FR-03 练习创建前校验 `mistake_item.ReviewStatus == Confirmed`，否则抛出 `MistakeValidationException`。
- [x] FR-04 练习创建时自动设置 `PracticeDate = DateTimeOffset.UtcNow`。
- [x] FR-05 练习创建时记录 `MistakeItemId`、`StudentId`、`StudentAnswer`、`IsCorrect`、`Comments`。
- [x] FR-06 `image_paths` 为可选参数，传入非空列表时写入 `ImagePaths`，不传或传空时保持默认空列表。
- [x] FR-07 练习创建成功后记录 `ProcessAction.Practiced` 操作日志。
- [x] FR-08 练习记录与操作日志在同一事务中写入，确保数据一致性。
- [x] FR-09 创建的练习记录通过 gRPC 返回完整的 `MistakePracticeDto`。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `AddPracticeAsync` 后，数据库中应插入一条 `mistake_practice` 记录，`Id != Guid.Empty`。
- AC-FR-02：传入不存在的 `mistake_item_id` 时，应抛出 `MistakeValidationException`，消息包含 "错题条目不存在"。
- AC-FR-03：传入 `ReviewStatus != Confirmed` 的错题条目时，应抛出 `MistakeValidationException`，消息包含 "只能对已确认的错题进行练习"。
- AC-FR-04：创建后的 `PracticeDate` 接近当前时间（误差 < 5s）。
- AC-FR-05：创建后的 `StudentAnswer`、`IsCorrect`、`Comments` 与入参一致。
- AC-FR-06：传入 `imagePaths = ["img1.jpg"]` 时，`practice.ImagePaths.Count == 1` 且 `ImagePaths[0] == "img1.jpg"`。
- AC-FR-07：创建成功后 `_processLogRepository.AddAsync` 被调用一次，`Action == (int)ProcessAction.Practiced`。
- AC-FR-08：若事务中操作日志写入失败，练习记录也应回滚，数据库中不存在任何新记录。
- AC-FR-09：gRPC 返回的 `MistakePracticeDto` 包含正确的 `id`、`mistake_item_id`、`student_id`、`practice_date`、`student_answer`、`is_correct`、`comments`、`image_paths` 字段。

## 非功能需求

- **性能**：添加练习操作 P95 < 200ms。
- **安全**：由上层 API 网关负责鉴权；student_id 不应在日志中以明文完整记录。
- **可靠性**：使用 `IUnitOfWork.SaveChangesAsync` 事务保护，确保练习记录与操作日志的原子性写入。
- **可观测性**：关键错误（错题不存在、状态不符）应通过 `ILogger` 记录 Warning 级别日志。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.AddPracticeAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：错题条目不存在、错题条目状态非 Confirmed、事务失败。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储、工作单元）均可替换。
- **禁止事项**：不得写入真实生产数据库；不得依赖时间戳做精确相等断言（应使用时间范围）。