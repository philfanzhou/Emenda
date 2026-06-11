# AddMistakeItem - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto message | PascalCase，Request/Response 后缀 | `AddMistakeItemRequest` |
| Proto field | snake_case | `student_id`, `source_upload_id` |
| gRPC 方法 | PascalCase，与 RPC 名一致 | `AddMistakeItem` |
| 领域服务方法 | PascalCase + Async 后缀 | `AddMistakeItemAsync` |
| 仓储方法 | PascalCase + Async 后缀 | `AddAsync` |
| 领域模型 | PascalCase | `MistakeItemModel`, `SourceRegionModel` |
| 数据库实体 | PascalCase + Entity 后缀 | `MistakeItemEntity` |
| 数据库列 | snake_case | `student_id`, `source_upload_id` |
| 数据库表 | snake_case | `mistake_item` |

## 日志和安全

### 日志

- 领域服务使用 `ILogger<MistakeDomainService>` 记录 Debug 级别日志
- gRPC 层使用 `ILogger<MistakeServiceImpl>` 记录 Debug/Warning 级别日志
- 验证失败记录 Warning 级别日志
- 不记录敏感信息（如完整学生身份信息）

### 安全

- `student_id` 作为业务标识，不包含个人隐私信息
- `source_upload_id` 为内部引用标识
- 错误消息不泄露内部实现细节

## 错误消息格式

| 触发条件 | 错误消息 | gRPC StatusCode |
|----------|---------|-----------------|
| studentId 为空 | 缺少学生ID | InvalidArgument |
| subject < 1 或 > 9 | 学科值无效（有效范围：1-9） | InvalidArgument |
| grade < 0 或 > 12 | 年级值无效（有效范围：0-12） | InvalidArgument |
| sourceUploadId 为空 | 缺少上传记录ID | InvalidArgument |
| sourceRegions 为空 | 至少需要一个图片区域 | InvalidArgument |
| 多个验证失败 | 错误消息以分号拼接 | InvalidArgument |
