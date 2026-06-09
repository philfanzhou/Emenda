# AnalyzeMistakeType — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；gRPC 层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；响应类型使用 proto 定义的 `MistakeTypeResponse`。
- **方法名**：动词开头，遵循 `[动词][名词]Async`，如 `AnalyzeMistakeTypeAsync`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_processLogRepository`、`_unitOfWork`）。
- **proto 字段**：使用 snake_case（如 `mistake_item_id`、`type_name`、`analysis_reason`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在分析日志中记录完整的 `content`、`correct_answer`、`student_answer` 内容。
- **日志级别**：
  - `Information`：分析操作完成时记录分析结果摘要。
  - `Warning`：非致命错误（如错题条目不存在）。
  - `Error`：事务失败、仓储写入失败。必须包含 `MistakeItemId` 结构化属性以便排查。
- **当前实现**：当前领域服务不记录 ILogger 日志；未来实装 AI 分析时应添加 Info 级别的分析结果日志。

## 错误消息格式约定

| 场景 | 消息文本（英文） |
| --- | --- |
| GUID 格式无效 | 由 `Guid.Parse` 抛出 `FormatException` |
| 错题条目不存在 | 不抛异常（静默返回分析结果） |

> 注：当前为占位实现，不进行输入参数校验（content/correct_answer/student_answer）；未来实装时应添加参数校验逻辑。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（仓储、工作单元）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null`。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `AnalyzeMistakeType_ShouldReturnHardcodedResult`。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。