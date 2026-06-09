# UpdateMistakeItem — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题条目更新是对已存在的错题条目进行字段修改的操作，通过 gRPC 接口接收更新请求，先在领域服务层查询条目，再对传入的有效字段应用部分更新，最后通过仓储接口持久化到 PostgreSQL 并返回更新后的完整条目。

**用户故事 1（教师修改条目）**：作为教师，当错题条目的学生归属、学科、年级或图片区域需要调整时，我可以通过 gRPC 传入条目 ID 和新字段值来进行部分更新；系统先查找条目、仅覆盖有效字段、保存并返回最新数据。

## 功能要求清单（可独立测试）

- [x] FR-01 能根据 ID 查找已有错题条目，条目不存在时返回 NotFound 错误。
- [x] FR-02 支持部分更新——student_id 仅当非空（非 null/whitespace）时才更新。
- [x] FR-03 支持部分更新——subject 仅当 > 0 时才更新。
- [x] FR-04 支持部分更新——grade 仅当 > 0 时才更新。
- [x] FR-05 支持部分更新——source_regions 仅当 count > 0 时才更新（含 BoundingBox 映射）。
- [x] FR-06 source_regions 更新时，每个 SourceRegion 的 BoundingBox 可为 null。
- [x] FR-07 更新成功后通过 `UpdateAsync + SaveChangesAsync` 持久化，返回更新后的完整 MistakeItemDto。
- [x] FR-08 更新操作不记录 process_log（与创建操作不同）。
- [x] FR-09 仓储 UpdateAsync 返回 false 时，返回 Internal 错误。

## 详细的验收标准（可自动验证）

- AC-FR-01：传入不存在的 ID，应抛出 `RpcException(StatusCode.NotFound)`，消息为 "错题条目不存在"。
- AC-FR-02：仅传入非空 student_id，其他字段传入空/零值，更新后 student_id 改变，其他字段保持原值不变。
- AC-FR-03：仅传入有效 subject（如 5），更新后 subject 变为 5，其他字段保持原值不变。
- AC-FR-04：仅传入有效 grade（如 8），更新后 grade 变为 8，其他字段保持原值不变。
- AC-FR-05：仅传入新 source_regions 列表，更新后 source_regions 改变，其他字段保持原值不变。
- AC-FR-06：传入含 BoundingBox 的 source_regions，更新后 BoundingBox 坐标应与输入一致。
- AC-FR-07：更新成功后 gRPC 返回的 `MistakeItemDto` 所有字段反映最新值。
- AC-FR-08：`UpdateMistakeItemAsync` 调用期间，`IProcessLogRepository.AddAsync` 未被调用。
- AC-FR-09：`IMistakeItemRepository.UpdateAsync` 返回 false 时，应抛出 `RpcException(StatusCode.Internal)` 消息为 "更新失败"。

## 非功能需求

- **性能**：更新操作 P95 < 100ms（一次查询 + 一次更新）。
- **安全**：仅教师/管理员可更新错题条目（由上层 API 网关负责鉴权）。
- **可靠性**：先查后改模式确保更新基于最新数据；`SaveChangesAsync` 保证写入的原子性。
- **可观测性**：条目不存在时通过 gRPC NotFound 状态码明确告知调用方。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.UpdateMistakeItemAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：条目不存在、UpdateAsync 返回 false。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储、工作单元）均可替换。
- **禁止事项**：不得写入真实生产数据库；不得依赖时间戳做精确相等断言。