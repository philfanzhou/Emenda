# LogMistakeProcess - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto message | PascalCase，Request/Response 后缀 | `LogMistakeProcessRequest` |
| Proto field | snake_case | `mistake_item_id`, `student_id`, `action`, `details` |
| gRPC 方法 | PascalCase，与 RPC 名一致 | `LogMistakeProcess` |
| 领域服务方法 | PascalCase + Async 后缀 | `LogMistakeProcessAsync` |
| 仓储方法 | PascalCase + Async 后缀 | `AddAsync`, `GetByIdAsync` |
| 领域模型 | PascalCase | `ProcessLogModel` |
| 数据库实体 | PascalCase + Entity 后缀 | `ProcessLogEntity` |
| 数据库列 | snake_case | `mistake_item_id`, `action`, `details`, `created_at` |
| 数据库表 | snake_case | `process_log` |

## 日志和安全

### 日志

- 领域服务使用 `ILogger<MistakeDomainService>` 记录日志
- 当前 `LogMistakeProcessAsync` 未显式记录操作日志（日志本身即为此功能）
- 建议对日志记录失败的情况记录 Warning 级别日志

### 安全

- 日志记录为追加模式，不支持修改和删除
- `student_id` 参数当前未存储，预留扩展
- 错题不存在时返回 `success=false` 而非异常，保证幂等性

## 错误消息格式

| 触发条件 | 错误消息 | gRPC StatusCode |
|----------|---------|-----------------|
| 错题不存在 | （不抛异常，返回 success=false） | — |

> 注：若 `Guid.Parse(request.MistakeItemId)` 格式无效，框架层会抛出格式异常。

## ProcessAction 枚举值参考

| 枚举值 | 数值 | 说明 |
|--------|------|------|
| UNSPECIFIED | 0 | 未指定 |
| Created | 1 | 创建 |
| Analyzed | 2 | 分析 |
| ReviewConfirmed | 3 | 审核确认 |
| ReviewRejected | 4 | 审核驳回 |
| Practiced | 5 | 练习 |
| Mastered | 6 | 掌握 |
