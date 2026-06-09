# GetMistakeItem — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：错题条目查询包含三个 gRPC 端点，分别支持按 ID 单条查询、按上传记录 ID 列表查询、及多条件分页查询。所有查询通过领域服务层调用仓储接口执行数据库查询，返回映射后的 DTO 列表。

**用户故事 1（按 ID 查询）**：作为教师，我通过错题条目 ID 查询单条错题的完整信息；条目不存在时系统明确返回 NotFound 错误。

**用户故事 2（按上传记录查询）**：作为教师，在审核某次上传时我需要查看该批次产生的所有错题条目，系统按 source_upload_id 返回完整列表。

**用户故事 3（分页条件查询）**：作为教师，我通过学生、学科、年级、审核状态等条件分页浏览错题列表，系统返回符合条件的分页结果。

## 功能要求清单（可独立测试）

- [x] FR-01 `GetMistakeItem`：根据 GUID ID 查询单条条目，条目不存在返回 NotFound。
- [x] FR-02 `GetMistakeItem`：查询成功返回包含所有字段的 `MistakeItemDto`。
- [x] FR-03 `GetMistakeItemsByUpload`：根据 source_upload_id 查询所有关联条目。
- [x] FR-04 `GetMistakeItemsByUpload`：返回 `MistakeItemListResponse`，含 `items` 列表。
- [x] FR-05 `GetMistakeItemList`：支持分页，默认 page=1、size=10。
- [x] FR-06 `GetMistakeItemList`：size 超过 100 时自动限制为 100。
- [x] FR-07 `GetMistakeItemList`：支持按 student_id、subject、grade、review_status 过滤。
- [x] FR-08 `GetMistakeItemList`：返回 `MistakeItemPageResult`，含 `items` 和 `page_meta`（page、size、total_count、total_pages）。
- [x] FR-09 所有查询返回的 `MistakeItemDto` 包含 source_regions（含 BoundingBox）、created_at、updated_at 等完整字段。

## 详细的验收标准（可自动验证）

### GetMistakeItem

- AC-FR-01：传入存在的 ID，返回 `MistakeItemDto`，所有字段非默认值。
- AC-FR-02：传入不存在的 ID，抛出 `RpcException(StatusCode.NotFound)`，消息为 "错题条目不存在"。
- AC-FR-03：返回的 `MistakeItemDto.source_regions` 中 BoundingBox 可为 null 或包含完整坐标。

### GetMistakeItemsByUpload

- AC-FR-04：传入存在的 source_upload_id，返回关联的所有条目，`items` 数量与数据库一致。
- AC-FR-05：传入不存在的 source_upload_id，返回 `items` 为空列表（不抛异常）。

### GetMistakeItemList

- AC-FR-06：不传任何过滤条件，page=1、size=10，返回第一页的 10 条记录和正确的 page_meta。
- AC-FR-07：传入 size=200，实际查询 size 应为 100（最大限制）。
- AC-FR-08：传入 student_id 过滤，仅返回该学生的条目。
- AC-FR-09：传入 review_status=REVIEW_STATUS_PENDING_REVIEW，仅返回待审核条目。
- AC-FR-10：传入 page=2、size=5，返回第二页的 5 条记录，page_meta.page=2。
- AC-FR-11：page_meta.total_pages 计算正确：`(total_count + size - 1) / size`（整数除法向上取整）。

## 非功能需求

- **性能**：单条查询 P95 < 50ms；列表查询 P95 < 200ms；分页查询 P95 < 200ms。
- **安全**：仅教师/管理员可查询错题条目（由上层 API 网关负责鉴权）。
- **可靠性**：查询为只读操作，不使用事务。
- **可观测性**：条目不存在时通过 gRPC NotFound 状态码明确告知调用方。

## 测试策略

- **覆盖率目标**：`MistakeDomainService` 三个查询方法代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：GetMistakeItem 条目不存在、GetMistakeItemList 空结果、GetMistakeItemList size > 100 截断。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储）均可替换。
- **禁止事项**：不得写入真实生产数据库；分页计算不要用浮点运算。