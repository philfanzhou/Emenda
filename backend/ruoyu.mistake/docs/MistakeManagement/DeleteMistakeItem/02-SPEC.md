# DeleteMistakeItem — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题条目删除是对错题条目及其关联数据进行物理删除的操作，通过 gRPC 接口接收条目 ID，在领域服务层先查找条目，再依次删除关联的练习记录、操作日志和错题条目自身，所有删除在单一事务中提交。

**用户故事 1（教师删除条目）**：作为教师，当某条错题不再需要时，我通过 gRPC 传入条目 ID 进行删除；系统自动清理关联的练习记录和操作日志，返回成功或失败的结果。

## 功能要求清单（可独立测试）

- [x] FR-01 能根据 ID 查找错题条目，条目不存在时返回 `success = false`（不抛异常）。
- [x] FR-02 删除前先通过 `IMistakePracticeRepository.RemoveByMistakeItemIdAsync` 删除关联的练习记录。
- [x] FR-03 删除前先通过 `IProcessLogRepository.RemoveByMistakeItemIdAsync` 删除关联的操作日志。
- [x] FR-04 调用 `IMistakeItemRepository.DeleteAsync` 删除错题条目自身。
- [x] FR-05 所有删除操作通过 `IUnitOfWork.SaveChangesAsync` 在同一事务中提交。
- [x] FR-06 删除成功返回 `BoolResponse { Success = true }`。
- [x] FR-07 任意审核状态（PendingReview、Confirmed、Rejected）的条目均可被删除。

## 详细的验收标准（可自动验证）

- AC-FR-01：传入不存在的 ID，`DeleteMistakeItemAsync` 返回 `false`，gRPC 层返回 `BoolResponse { Success = false }`。
- AC-FR-02：删除成功后，`RemoveByMistakeItemIdAsync` 被调用一次（参数为正确 ID）。
- AC-FR-03：删除成功后，`RemoveByMistakeItemIdAsync`（操作日志）被调用一次（参数为正确 ID）。
- AC-FR-04：删除成功后，`DeleteAsync` 被调用一次（参数为正确 ID）。
- AC-FR-05：删除成功后，`SaveChangesAsync` 被调用一次。
- AC-FR-06：删除成功后 gRPC 层返回 `BoolResponse { Success = true }`，无 ErrorMessage。
- AC-FR-07：Confirmed 状态的条目删除成功（与 PendingReview 状态行为一致）。
- AC-FR-08：事务中任意步骤失败（如 SaveChangesAsync 抛异常），所有删除操作回滚。

## 非功能需求

- **性能**：删除操作 P95 < 200ms（包括三次删除 + 一次事务提交）。
- **安全**：仅教师/管理员可删除错题条目（由上层 API 网关负责鉴权）。
- **可靠性**：使用 `IUnitOfWork.SaveChangesAsync` 事务保护，确保三级删除的原子性。
- **可观测性**：条目不存在时返回 false 而非抛异常，调用方可根据 success 字段判断结果。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.DeleteMistakeItemAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：条目不存在、事务失败。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储、工作单元）均可替换。
- **禁止事项**：不得写入真实生产数据库；不得依赖时间戳做精确相等断言。