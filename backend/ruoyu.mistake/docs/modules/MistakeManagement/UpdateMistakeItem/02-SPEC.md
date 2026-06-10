# UpdateMistakeItem - 功能规格

## 功能概述

教师通过 gRPC 接口更新已有错题条目的部分信息。系统采用部分更新语义，仅更新请求中非空/非零的字段，更新后返回最新数据。

## 用户故事

作为教师，我希望能够更新已有错题条目的信息，以便修正录入错误或补充错题详情。

## 功能要求

### FR-01 查询待更新条目

系统先根据 `id` 查询错题条目，若不存在则返回 `NotFound` 错误。

### FR-02 部分更新字段

系统仅更新请求中非空/非零的字段：
- `student_id`：非空时更新
- `subject`：大于 0 时更新
- `grade`：大于 0 时更新
- `source_regions`：Count > 0 时更新（整体替换）

### FR-03 返回最新数据

更新成功后，系统重新查询该条目并返回最新的 `MistakeItemDto`。

### FR-04 错误处理

- 错题不存在 → `RpcException(StatusCode.NotFound, "错题条目不存在")`
- 验证失败 → `MistakeValidationException` 转换为 `RpcException(StatusCode.InvalidArgument)`
- 更新失败 → `RpcException(StatusCode.Internal, "更新失败")`

## 验收标准

### AC-FR-01

**Given** 请求中提供了不存在的错题 ID
**When** 调用 UpdateMistakeItem RPC
**Then** 返回 gRPC 错误，状态码为 `NotFound`，消息为"错题条目不存在"

### AC-FR-02

**Given** 存在 ID 为 "abc" 的错题条目，请求中仅提供 student_id="new-student"
**When** 调用 UpdateMistakeItem RPC
**Then** 仅 student_id 被更新，subject、grade、source_regions 保持原值

### AC-FR-03

**Given** 成功更新了一条错题
**When** UpdateMistakeItem 返回结果
**Then** 返回的 MistakeItemDto 包含更新后的最新数据

### AC-FR-04

**Given** 领域服务 UpdateMistakeItemAsync 返回 false
**When** 调用 UpdateMistakeItem RPC
**Then** 返回 gRPC 错误，状态码为 `Internal`，消息为"更新失败"

## 非功能需求

- 无特殊性能要求，单次更新操作应在 500ms 内完成
- 部分更新语义减少不必要的数据覆盖

## 测试策略

- 单元测试：验证领域服务 `UpdateMistakeItemAsync` 的更新逻辑
- 集成测试：验证 gRPC 层的部分更新语义和错误转换
