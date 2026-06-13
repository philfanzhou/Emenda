# ReviewMistakeItem - 约定与规范

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| Proto RPC | PascalCase 动词+名词 | ReviewMistakeItem |
| Proto Message | PascalCase 请求/响应后缀 | ReviewMistakeItemRequest |
| Proto Field | snake_case | review_status, reviewer_id |
| 领域方法 | PascalCase Async 后缀 | ReviewMistakeItemAsync |
| 仓储方法 | PascalCase Async 后缀 | GetByIdAsync, UpdateAsync |
| 模型属性 | PascalCase | ReviewStatus, ReviewerId |
| 数据库列 | snake_case | review_status, reviewer_id |

## 日志要求

| 级别 | 场景 | 示例 |
|------|------|------|
| Debug | 图片迁移成功 | `ReviewMistakeItem: Migrated image from {OldPath} to {NewPath}` |
| Warning | 领域层验证失败 | 领域层不单独记录，由 gRPC 层处理 |

## 安全要求

- reviewerId 需从认证上下文获取，不应由客户端自由传入（当前实现由客户端传入，需后续改进）
- 图片迁移操作涉及 OSS 写入和删除，需确保调用方有相应权限
- 不在错误消息中暴露内部实现细节或数据库结构

## 错误消息格式

| 错误场景 | gRPC StatusCode | 错误消息 | 来源 |
|----------|-----------------|----------|------|
| review_status 非 Confirmed/Rejected | InvalidArgument | 审核结果只能是确认或驳回 | gRPC层 |
| 错题非 PendingReview 状态 | InvalidArgument | 只能审核待审核状态的错题 | 领域层 |
| 审核结果非 Confirmed/Rejected（领域层） | InvalidArgument | 审核结果只能是确认或驳回 | 领域层 |
| 错题不存在 | - | success=false, error_message="错题不存在" | gRPC层 |

## 图片迁移约定

| 规则 | 说明 |
|------|------|
| 迁移条件 | review_status=Confirmed 且 SourceImagePath 以 "uploads/" 开头（不区分大小写） |
| 路径转换 | `uploads/xxx` → `mistakes/xxx`（替换前缀 "uploads" 为 "mistakes"） |
| OSS 操作顺序 | 先 Copy 后 Delete |
| 非迁移路径 | 不以 "uploads/" 开头的路径不迁移，但仍收集到 removed_image_paths |
| 空路径跳过 | SourceImagePath 为空或空白时跳过 |
