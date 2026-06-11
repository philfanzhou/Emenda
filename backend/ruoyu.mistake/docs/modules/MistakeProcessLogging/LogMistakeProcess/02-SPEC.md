# LogMistakeProcess - 功能规格

## 功能概述

通过 gRPC 接口手动记录错题的操作日志，指定错题 ID、学生 ID、操作类型和详情。系统验证错题存在后创建日志记录。

## 用户故事

作为系统，我希望能够手动记录错题的操作日志，以便追踪错题生命周期中的自定义事件和操作详情。

## 功能要求

### FR-01 验证错题存在性

系统根据 `mistake_item_id` 查询错题条目，若不存在则返回 `success=false`。

### FR-02 创建操作日志

系统创建 `ProcessLogModel` 并保存：
- `Id` = `Guid.NewGuid()`
- `MistakeItemId` = 请求中的 mistake_item_id
- `StudentId` = 请求中的 student_id
- `Action` = 请求中的 ProcessAction 枚举值
- `Details` = 请求中的 details 字符串
- `CreatedAt` = `DateTimeOffset.UtcNow`

### FR-03 返回操作结果

- 成功时返回 `BoolResponse { success = true }`
- 错题不存在时返回 `BoolResponse { success = false }`

## 验收标准

### AC-FR-01

**Given** 请求中提供了不存在的 mistake_item_id
**When** 调用 LogMistakeProcess RPC
**Then** 返回 `BoolResponse { success = false }`

### AC-FR-02

**Given** 请求中提供了有效的 mistake_item_id、student_id、action=Practiced、details="Custom log entry"
**When** 调用 LogMistakeProcess RPC
**Then** 返回 `BoolResponse { success = true }`，且 process_log 表中新增一条对应记录

### AC-FR-03

**Given** 成功记录了一条操作日志
**When** 查询该错题的 ProcessLog 历史
**Then** 能找到该条日志，Action 和 Details 与请求一致

## 非功能需求

- 日志记录操作应在 200ms 内完成
- 日志为追加模式，不支持修改和删除

## 测试策略

- 单元测试：验证领域服务 `LogMistakeProcessAsync` 的存在性验证和日志创建逻辑
- 集成测试：验证 gRPC 层的请求映射和响应格式
