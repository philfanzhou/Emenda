# 图片路径查询 - 约定

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| gRPC 方法 | PascalCase，动词+名词 | `GetAllReferencedImagePaths` |
| 领域服务方法 | PascalCase，Async 后缀 | `GetAllReferencedImagePathsAsync` |
| 仓储方法 | PascalCase，Async 后缀 | `GetAllAsync` |
| Proto message | PascalCase | `GetAllReferencedImagePathsResponse` |
| Proto 字段 | snake_case | `image_paths` |
| 返回类型 | `HashSet<string>` | 领域层使用 HashSet 表达去重语义 |

## 日志和安全

### 日志

- 当前 gRPC 层未添加 LogDebug/LogInformation 日志
- 建议补充：调用时记录请求，返回时记录路径数量

### 安全

- 无鉴权逻辑（由 gRPC 拦截器统一处理）
- 无参数，不存在输入注入风险
- 返回所有图片路径，可能暴露存储结构。生产环境应考虑访问控制

## 错误消息格式

| 场景 | 当前行为 | 建议改进 |
|------|---------|---------|
| 数据库异常 | EF Core 抛出异常，gRPC 返回 Unknown | 由全局异常处理兜底 |
| 无数据 | 返回空列表 | ✅ 正确行为 |

### 错误消息表

| 错误码 | gRPC Status | 消息模板 | 触发条件 |
|--------|-------------|---------|---------|
| OK | OK | (空列表) | 无图片数据 |
| UNKNOWN | Unknown | (EF Core 异常信息) | 数据库查询失败 |

## 性能注意事项

| 场景 | 当前行为 | 风险 |
|------|---------|------|
| 全表加载 | `GetAllAsync()` 加载所有 MistakeItem 和 MistakePractice 到内存 | 数据量大时内存占用高 |
| 无分页 | 返回全量路径 | 路径数量极多时响应体积大 |
| 无缓存 | 每次调用都查数据库 | 频繁调用时数据库压力大 |
