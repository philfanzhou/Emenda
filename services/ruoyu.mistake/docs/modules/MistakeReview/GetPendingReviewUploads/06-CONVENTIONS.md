# GetPendingReviewUploads - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto RPC | PascalCase 动词+名词 | GetPendingReviewUploads |
| Proto Message | PascalCase 请求/响应后缀 | GetPendingReviewUploadsRequest |
| Proto Field | snake_case | source_upload_id, pending_count |
| 领域方法 | PascalCase Async 后缀 | GetPendingReviewUploadsAsync |
| 仓储方法 | PascalCase Async 后缀 | GetPendingReviewUploadsAsync |
| 模型属性 | PascalCase | SourceUploadId, PendingCount |
| 数据库列 | snake_case | source_upload_id, review_status |

## 日志要求

| 级别 | 场景 | 示例 |
|------|------|------|
| Debug | 查询参数记录 | 可选，当前未实现 |

## 安全要求

- 不暴露学生个人信息到外部日志
- 分页 size 上限 100 防止大量数据泄露
- subjects 参数需验证元素在 1-9 范围内（当前未验证，需后续改进）

## 错误消息格式

| 错误场景 | gRPC StatusCode | 错误消息 | 说明 |
|----------|-----------------|----------|------|
| 无 | - | - | 当前无显式错误场景，空结果返回空列表 |

## 分页约定

| 参数 | 默认值 | 上限 | 修正规则 |
|------|--------|------|----------|
| page | 1 | - | page<=0 → 1 |
| size | 10 | 100 | size<=0 → 10, size>100 → 100 |

## 时间格式约定

| 场景 | 格式 | 示例 |
|------|------|------|
| 有效 DateTimeOffset | ISO 8601 UTC | 2024-01-15T08:30:00.000Z |
| DateTimeOffset.MinValue | 空字符串 | "" |

## SQLite 兼容性约定

| 操作 | 处理方式 |
|------|----------|
| DateTimeOffset 的 Min 聚合 | 客户端 LINQ 聚合 |
| DateTimeOffset 的排序 | 客户端 LINQ 排序 |
| 分页 | 客户端 Skip/Take（聚合后） |
