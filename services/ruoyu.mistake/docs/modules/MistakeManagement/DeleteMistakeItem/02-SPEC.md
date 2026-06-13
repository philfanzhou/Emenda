# DeleteMistakeItem - 功能规格

## 功能概述

教师通过 gRPC 接口删除指定错题条目及其所有关联数据（练习记录和操作日志）。系统按固定顺序删除子表数据，确保数据完整性。

## 用户故事

作为教师，我希望能够删除不再需要的错题条目，以便清理无效或误录入的数据并释放关联资源。

## 功能要求

### FR-01 查询待删除条目

系统先根据 `id` 查询错题条目，若不存在则返回 `success=false`。

### FR-02 级联删除子表数据

系统按以下顺序删除关联数据：
1. 删除 `mistake_practice` 表中 `mistake_item_id` 匹配的记录
2. 删除 `process_log` 表中 `mistake_item_id` 匹配的记录
3. 删除 `mistake_item` 表中的主记录

### FR-03 统一事务提交

所有删除操作在同一事务中完成，通过 `IUnitOfWork.SaveChangesAsync()` 统一提交。

### FR-04 错误处理

- `MistakeValidationException` → `RpcException(StatusCode.InvalidArgument)`

## 验收标准

### AC-FR-01

**Given** 请求中提供了不存在的错题 ID
**When** 调用 DeleteMistakeItem RPC
**Then** 返回 `BoolResponse { success = false }`

### AC-FR-02

**Given** 存在 ID 为 "abc" 的错题条目，且关联了 2 条练习记录和 3 条操作日志
**When** 调用 DeleteMistakeItem RPC
**Then** 按顺序删除 practices、process_logs、mistake_item，返回 `BoolResponse { success = true }`

### AC-FR-03

**Given** 错题条目审核状态为 Confirmed
**When** 调用 DeleteMistakeItem RPC
**Then** 成功删除并返回 `success = true`

### AC-FR-04

**Given** 错题条目审核状态为 PendingReview
**When** 调用 DeleteMistakeItem RPC
**Then** 成功删除并返回 `success = true`

## 非功能需求

- 删除操作应在 1s 内完成
- 显式删除子表数据，即使数据库有 CASCADE 配置，保证代码层逻辑清晰

## 测试策略

- 单元测试：验证领域服务 `DeleteMistakeItemAsync` 的删除顺序和返回值
- 集成测试：验证 gRPC 层的错误转换
