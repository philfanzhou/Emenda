# 错题条目查询 — 详细需求规格 (SPEC)

## 功能概述

根据错题 ID 查询单条错题条目的完整信息，包括学生ID、学科、年级、图片区域、审核状态等。这是错题管理模块的基础查询接口。

## 功能要求清单

- [ ] FR-01: 响应 `GetMistakeItem` gRPC 请求，返回 `MistakeItemDto`
- [ ] FR-02: 传入有效 ID 时返回对应的错条条目
- [ ] FR-03: 传入不存在的 ID 时返回 gRPC NotFound 错误，消息为"错题条目不存在"

## 验收标准

### AC-FR-02: 有效 ID 查询

- **Given**: 数据库中存在 ID 为 "abc-123" 的错题条目
- **When**: 调用 `GetMistakeItem(Id="abc-123")`
- **Then**: 返回 `MistakeItemDto`，`Id` 为 "abc-123"，字段完整

### AC-FR-03: 不存在的 ID

- **Given**: 数据库中不存在 ID 为 "not-exist" 的错题条目
- **When**: 调用 `GetMistakeItem(Id="not-exist")`
- **Then**: 抛出 `RpcException(StatusCode.NotFound, "错题条目不存在")`

## 非功能需求

- **性能**: 主键查询，响应时间应在 10ms 以内
- **可靠性**: 数据库不可用时返回 gRPC Internal 错误

## 测试策略

- 单元测试覆盖 Domain Service 的 null 返回场景
- 集成测试验证 gRPC 层的 NotFound 映射
