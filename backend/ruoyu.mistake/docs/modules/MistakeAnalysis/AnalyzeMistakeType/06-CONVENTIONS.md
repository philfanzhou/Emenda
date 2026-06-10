# 错题类型分析 - 约定

## 命名约定

| 层级 | 约定 | 示例 |
|------|------|------|
| gRPC 方法 | PascalCase，动词+名词 | `AnalyzeMistakeType` |
| 领域服务方法 | PascalCase，Async 后缀 | `AnalyzeMistakeTypeAsync` |
| Proto message | PascalCase | `AnalyzeMistakeRequest`, `MistakeTypeResponse` |
| Proto 字段 | snake_case | `mistake_item_id`, `type_name`, `analysis_reason` |
| 错误类型编号 | int 常量 | type=5 (其他错误) |

## 日志和安全

### 日志

- 当前 gRPC 层未添加 LogDebug/LogInformation 日志
- 建议补充：调用时记录 mistakeItemId，返回时记录分析结果
- ProcessLog 记录格式：`"Mistake analyzed as {typeName}"`

### 安全

- 无鉴权逻辑（由 gRPC 拦截器统一处理）
- mistakeItemId 为 Guid，不易被枚举
- 返回数据不含敏感信息

## 错误消息格式

| 场景 | 当前行为 | 建议改进 |
|------|---------|---------|
| 无效 Guid 格式 | `Guid.Parse` 抛出 `FormatException`，gRPC 返回 Internal | 应捕获并返回 `StatusCode.InvalidArgument` + 友好消息 |
| 错题不存在 | 静默返回硬编码值，不记录日志 | ✅ 当前设计合理，但未来实现真正分析时可能需要报错 |

### 错误消息表

| 错误码 | gRPC Status | 消息模板 | 触发条件 |
|--------|-------------|---------|---------|
| INVALID_ARGUMENT | InvalidArgument | "无效的错题ID格式" | request.MistakeItemId 不是有效 Guid |

## 已知技术债

| 项目 | 说明 | 影响 |
|------|------|------|
| 领域层返回 gRPC 类型 | `AnalyzeMistakeTypeAsync` 返回 `MistakeTypeResponse`（Contract 层类型） | 领域层耦合契约层，影响可测试性和可维护性 |
| Proto 参数未使用 | content/correct_answer/student_answer 在 gRPC 层被忽略 | API 语义与实现不一致 |
| 硬编码分析结果 | 始终返回 type=5 | 功能不可用，仅作为占位 |
