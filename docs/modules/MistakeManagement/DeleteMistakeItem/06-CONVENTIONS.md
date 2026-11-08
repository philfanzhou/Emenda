# DeleteMistakeItem - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto message | PascalCase，Request/Response 后缀 | `IdRequest`, `BoolResponse` |
| Proto field | snake_case | `id`, `success`, `error_message` |
| gRPC 方法 | PascalCase，与 RPC 名一致 | `DeleteMistakeItem` |
| 领域服务方法 | PascalCase + Async 后缀 | `DeleteMistakeItemAsync` |
| 仓储方法 | PascalCase + Async 后缀 | `DeleteAsync`, `RemoveByMistakeItemIdAsync` |
| 数据库列 | snake_case | `mistake_item_id` |
| 数据库表 | snake_case | `mistake_item`, `mistake_practice`, `process_log` |

## 日志和安全

### 日志

- 领域服务使用 `ILogger<MistakeDomainService>` 记录日志
- 删除操作为关键操作，建议记录 Info 级别日志（当前代码未显式记录删除日志）
- 不记录敏感信息

### 安全

- 删除操作不可逆，建议在业务层增加权限校验
- 错题不存在时返回 `success=false` 而非异常，保证幂等性
- 显式删除子表数据，避免依赖数据库 CASCADE 导致数据不一致

## 错误消息格式

| 触发条件 | 错误消息 | gRPC StatusCode |
|----------|---------|-----------------|
| MistakeValidationException | 原始异常消息 | InvalidArgument |
| 错题不存在 | （不抛异常，返回 success=false） | — |
