# GetMistakePractices - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto message | PascalCase，Request/Response 后缀 | `IdRequest`, `MistakePracticeListResponse` |
| Proto field | snake_case | `id`, `practices` |
| gRPC 方法 | PascalCase，与 RPC 名一致 | `GetMistakePractices` |
| 领域服务方法 | PascalCase + Async 后缀 | `GetPracticesAsync` |
| 仓储方法 | PascalCase + Async 后缀 | `GetByMistakeItemIdAsync` |
| 领域模型 | PascalCase | `MistakePracticeModel` |
| 数据库实体 | PascalCase + Entity 后缀 | `MistakePracticeEntity` |
| 数据库列 | snake_case | `mistake_item_id`, `practice_date` |
| 数据库表 | snake_case | `mistake_practice` |

## 日志和安全

### 日志

- 当前查询操作未显式记录日志
- 建议对查询耗时较长的操作记录 Debug 级别日志

### 安全

- 查询操作不涉及敏感数据修改
- 建议在业务层增加数据权限校验（学生只能查看自己的练习记录）
- 当前无分页，大量数据场景下可能存在性能风险

## 错误消息格式

| 触发条件 | 错误消息 | gRPC StatusCode |
|----------|---------|-----------------|
| 无（查询操作不抛出业务异常） | — | — |

> 注：若 `Guid.Parse(request.Id)` 格式无效，框架层会抛出格式异常。
