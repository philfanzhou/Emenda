# GetPendingReviewUploads — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`。
- **类名**：领域服务采用 `MistakeDomainService`；模型采用 `PendingReviewUploadModel`；仓储接口采用 `IMistakeItemRepository`；仓储实现采用 `MistakeItemRepository`。
- **方法名**：动词开头，遵循 `[动词][名词]Async`，如 `GetPendingReviewUploadsAsync`。
- **返回值类**：gRPC 层使用 proto 定义的响应类型（如 `PendingReviewUploadListResponse`、`PendingReviewUploadDto`、`PageMeta`）；领域层使用 C# 元组 `(List<T>, int TotalCount)`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_mistakeService`）。
- **数据库**：错题表 `mistake_item` 小写蛇形；聚合查询使用 `source_upload_id` 分组。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录完整的 `SourceUploadId` 列表；仅记录请求参数摘要（page/size/subjects）。
- **日志级别**：
  - `Information`：无需记录（只读查询，频率高）。
  - `Warning`：非预期但可恢复的错误。
  - `Error`：仓储查询失败。必须包含 `page`、`size`、`subjects` 结构化属性以便排查。
- **错误消息**：查询为只读操作，无业务错误消息；仓储异常由 gRPC 拦截器统一处理。

## 错误消息格式约定

| 场景 | 消息文本（英文） |
| --- | --- |
| — | 本功能为只读查询，无面向用户的业务错误消息 |

> 注：分页参数修正为静默修正，不抛出异常也不记录日志。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（仓储）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null/Empty`；避免使用第三方 fluent 断言库以保持项目一致性。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `GetPendingReviewUploadsAsync_ReturnsPaginated`。
- **确定性**：Mock 仓储返回固定数据，不依赖真实数据库时间。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 分页参数修正逻辑应与仓储调用在同一方法中，避免分散。
- 领域服务中禁止直接操作 `DbContext` 或执行 SQL；所有数据访问必须通过仓储接口。
- gRPC 层负责 DTO 映射和时间格式转换（`DateTimeOffset` → `string`）。