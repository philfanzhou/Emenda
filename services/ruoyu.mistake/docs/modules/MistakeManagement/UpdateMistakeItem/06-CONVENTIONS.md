# UpdateMistakeItem - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto message | PascalCase，Request/Response 后缀 | `UpdateMistakeItemRequest` |
| Proto field | snake_case | `student_id`, `source_regions` |
| gRPC 方法 | PascalCase，与 RPC 名一致 | `UpdateMistakeItem` |
| 领域服务方法 | PascalCase + Async 后缀 | `UpdateMistakeItemAsync` |
| 仓储方法 | PascalCase + Async 后缀 | `UpdateAsync`, `GetByIdAsync` |
| 领域模型 | PascalCase | `MistakeItemModel`, `SourceRegionModel` |
| 数据库实体 | PascalCase + Entity 后缀 | `MistakeItemEntity` |
| 数据库列 | snake_case | `student_id`, `updated_at` |
| 数据库表 | snake_case | `mistake_item` |

## 日志和安全

### 日志

- 领域服务使用 `ILogger<MistakeDomainService>` 记录 Debug 级别日志
- gRPC 层使用 `ILogger<MistakeServiceImpl>` 记录日志
- 验证失败记录 Warning 级别日志
- 不记录敏感信息

### 安全

- 更新操作前必须确认条目存在，防止越权操作
- `UpdatedAt` 字段使用 `[ConcurrencyCheck]` 实现乐观并发控制
- 部分更新语义避免意外覆盖未提供的字段

## 错误消息格式

| 触发条件 | 错误消息 | gRPC StatusCode |
|----------|---------|-----------------|
| 错题 ID 不存在（查询阶段） | 错题条目不存在 | NotFound |
| MistakeValidationException | 原始异常消息 | InvalidArgument |
| 领域服务返回 false（更新失败） | 更新失败 | Internal |
| 更新后重新查询返回 null | 错题条目不存在 | NotFound |
