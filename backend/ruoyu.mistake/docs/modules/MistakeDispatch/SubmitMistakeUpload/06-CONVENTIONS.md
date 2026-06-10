# SubmitMistakeUpload - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto RPC | PascalCase 动词+名词 | SubmitMistakeUpload |
| Proto Message | PascalCase 请求/响应后缀 | SubmitMistakeUploadRequest |
| Proto Field | snake_case | student_id, source_upload_id |
| 领域方法 | PascalCase Async 后缀 | SubmitMistakeUploadAsync |
| 仓储方法 | PascalCase Async 后缀 | AddAsync, GetByIdAsync |
| 模型属性 | PascalCase | StudentId, SourceRegions |
| 数据库列 | snake_case | student_id, source_upload_id |

## 日志要求

| 级别 | 场景 | 示例 |
|------|------|------|
| Debug | 方法入口，记录关键参数 | `SubmitMistakeUpload called: studentId={StudentId}, subject={Subject}...` |
| Debug | 方法成功返回，记录结果 | `SubmitMistakeUpload success: created {ItemCount} items, ids={ItemIds}` |
| Debug | 领域层创建条目 | `SubmitMistakeUploadAsync: Creating item with Id={ItemId}...` |
| Warning | 验证失败 | `SubmitMistakeUpload validation failed: {Message}` |

## 安全要求

- studentId 和 sourceUploadId 不得记录完整值到外部日志系统
- imagePaths 为用户上传路径，需防止路径遍历攻击（由上游上传服务保证）
- 不在错误消息中暴露内部实现细节

## 错误消息格式

| 错误场景 | gRPC StatusCode | 错误消息 | 来源 |
|----------|-----------------|----------|------|
| studentId 为空 | InvalidArgument | 缺少学生ID | gRPC层 + 领域层 |
| subject 超出范围 | InvalidArgument | 学科值无效（有效范围：1-9） | gRPC层 + 领域层 |
| grade 超出范围 | InvalidArgument | 年级值无效（有效范围：1-12） | gRPC层 + 领域层 |
| sourceUploadId 为空 | InvalidArgument | 缺少上传记录ID | gRPC层 + 领域层 |
| imagePaths 为空 | InvalidArgument | 至少需要提供一张图片 | gRPC层 + 领域层 |
| 多个验证错误 | InvalidArgument | 错误消息用分号分隔 | 领域层 |
