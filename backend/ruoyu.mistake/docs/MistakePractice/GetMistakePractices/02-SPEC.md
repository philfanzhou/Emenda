# GetMistakePractices — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：获取错题练习记录是错题学习数据查询的基础操作，通过 gRPC 接口接收错题条目 ID，在领域服务层通过仓储接口查询并返回该错题的所有练习记录，不涉及业务逻辑校验或状态转换。

**用户故事 1（学生查看练习历史）**：作为学生，我要查看某道错题的全部练习记录；系统根据错题条目 ID 查询数据库，返回包含答案、正确性和时间的完整练习列表。

## 功能要求清单（可独立测试）

- [x] FR-01 能根据 `mistake_item_id` 查询该错题的所有练习记录。
- [x] FR-02 返回的每条记录包含 `Id`、`MistakeItemId`、`StudentId`、`PracticeDate`、`ImagePaths`、`StudentAnswer`、`IsCorrect`、`Comments` 完整字段。
- [x] FR-03 若错题没有练习记录，返回空列表（不抛异常）。
- [x] FR-04 查询操作不产生副作用，不修改任何数据。
- [x] FR-05 gRPC 返回类型为 `MistakePracticeListResponse`，包含 `repeated MistakePracticeDto practices`。
- [x] FR-06 `id` 参数由 gRPC 的 `IdRequest` 传入，代表 `mistake_item_id` 的 GUID 字符串。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `GetPracticesAsync(mistakeItemId)` 后，返回的列表仅包含 `MistakeItemId == mistakeItemId` 的记录。
- AC-FR-02：返回的 `MistakePracticeDto` 中 `id` 不为空字符串、`mistake_item_id` 与入参一致、`practice_date` 为有效的 ISO 8601 格式字符串。
- AC-FR-03：传入无练习记录的错题条目 ID 时，gRPC 返回的 `MistakePracticeListResponse.Practices` 为空的 `repeated` 集合。
- AC-FR-04：查询操作不会调用 `_unitOfWork.SaveChangesAsync`，也不会写入任何数据。
- AC-FR-05：gRPC 方法返回类型与 proto 定义一致：`MistakePracticeListResponse`。
- AC-FR-06：传入非 GUID 格式的 `id` 时，`Guid.Parse` 抛出异常（由 gRPC 运行时处理）。

## 非功能需求

- **性能**：查询操作 P95 < 50ms。
- **安全**：由上层 API 网关负责鉴权，确保仅允许查看有权限的错题练习记录。
- **可靠性**：查询操作只读不写，无需事务保护。
- **可观测性**：无需额外日志记录（简单查询操作）。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.GetPracticesAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：查询无练习记录的错题条目（返回空列表）。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；仓储依赖可替换。
- **禁止事项**：不得写入真实生产数据库。