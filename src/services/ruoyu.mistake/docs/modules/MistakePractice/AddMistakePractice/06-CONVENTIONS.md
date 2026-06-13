# AddMistakePractice - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto message | PascalCase，Request/Response 后缀 | `AddMistakePracticeRequest` |
| Proto field | snake_case | `mistake_item_id`, `image_paths`, `is_correct` |
| gRPC 方法 | PascalCase，与 RPC 名一致 | `AddMistakePractice` |
| 领域服务方法 | PascalCase + Async 后缀 | `AddPracticeAsync` |
| 仓储方法 | PascalCase + Async 后缀 | `AddAsync`, `GetByIdAsync` |
| 领域模型 | PascalCase | `MistakePracticeModel` |
| 数据库实体 | PascalCase + Entity 后缀 | `MistakePracticeEntity` |
| 数据库列 | snake_case | `mistake_item_id`, `practice_date`, `image_paths` |
| 数据库表 | snake_case | `mistake_practice` |

## 日志和安全

### 日志

- 领域服务使用 `ILogger<MistakeDomainService>` 记录日志
- 验证失败记录 Warning 级别日志
- 不记录敏感信息

### 安全

- 只允许对 Confirmed 状态的错题进行练习，防止对未审核数据产生练习记录
- `mistake_item_id` 和 `student_id` 为业务标识，不包含个人隐私信息
- 练习记录通过外键关联错题，CASCADE 删除保证数据一致性

## 错误消息格式

| 触发条件 | 错误消息 | gRPC StatusCode |
|----------|---------|-----------------|
| 错题条目不存在 | 错题条目不存在 | InvalidArgument |
| 错题未确认（ReviewStatus != Confirmed） | 只能对已确认的错题进行练习 | InvalidArgument |
