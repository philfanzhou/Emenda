# 错题处理历史查询 - 约定

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| gRPC 方法 | PascalCase，动词+名词 | `GetMistakeProcessHistory` |
| 领域服务方法 | PascalCase，Async 后缀 | `GetMistakeProcessHistoryAsync` |
| 仓储方法 | PascalCase，Async 后缀 | `GetByMistakeItemIdAsync` |
| Proto message | PascalCase | `ProcessLogListResponse` |
| Proto 字段 | snake_case | `mistake_item_id`, `created_at` |
| 领域模型 | PascalCase 属性 | `MistakeItemId`, `CreatedAt` |
| 数据库列 | snake_case | `mistake_item_id`, `created_at` |

## 日志和安全

### 日志

- 当前 gRPC 层未添加专门的 LogDebug/LogInformation 日志
- 建议补充：调用时记录 mistakeItemId，返回时记录日志条数

### 安全

- 无鉴权逻辑（由 gRPC 拦截器统一处理）
- mistakeItemId 为 Guid，不易被枚举
- 返回数据不含敏感信息（仅操作记录）

## 错误消息格式

| 场景 | 当前行为 | 建议改进 |
|------|---------|---------|
| 无效 Guid 格式 | `Guid.Parse` 抛出 `FormatException`，gRPC 返回 Internal | 应捕获并返回 `StatusCode.InvalidArgument` + 友好消息 |
| 错题无日志 | 返回空列表，不报错 | ✅ 正确行为 |
| 数据库异常 | EF Core 抛出异常，gRPC 返回 Unknown | 由全局异常处理兜底 |

### 错误消息表

| 错误码 | gRPC Status | 消息模板 | 触发条件 |
|--------|-------------|---------|---------|
| INVALID_ARGUMENT | InvalidArgument | "无效的错题ID格式" | request.Id 不是有效 Guid |
| OK | OK | (空列表) | 无日志记录 |
