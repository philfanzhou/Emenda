# GetMistakeProcessHistory — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；gRPC 层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；响应类型使用 proto 定义的 `ProcessLogListResponse`、`ProcessLogDto`。
- **方法名**：动词开头，遵循 `[动词][名词]Async`，如 `GetMistakeProcessHistoryAsync`、`GetByMistakeItemIdAsync`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_processLogRepository`）。
- **proto 字段**：`ProcessLogDto` 使用 snake_case（如 `mistake_item_id`、`created_at`）。

## 日志和安全要求

- **禁止记录敏感信息**：查询操作不记录日志；若未来添加日志，不得记录完整的日志详情列表。
- **日志级别**：当前为只读查询，不记录日志。
- **安全**：返回的 `ProcessLogDto` 中 `details` 字段可能包含用户输入内容，不应在前端直接以 HTML 形式渲染（防 XSS）。

## 错误消息格式约定

| 场景 | 消息文本（英文） |
| --- | --- |
| GUID 格式无效 | 由 `Guid.Parse` 抛出 `FormatException` |
| 错题条目无日志 | 返回空 `ProcessLogListResponse`（不抛异常） |

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；`IProcessLogRepository` 必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null/Empty`。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `GetMistakeProcessHistory_ShouldReturnLogs`。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- `ProcessLogDto.created_at` 映射时使用 ISO 8601 格式（`yyyy-MM-dd'T'HH:mm:ss.fff'Z'`），空值用 `string.Empty` 表示。