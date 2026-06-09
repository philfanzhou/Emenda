# LogMistakeProcess — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`。
- **类名**：领域服务采用 `MistakeDomainService`；模型采用 `ProcessLogModel`；仓储接口采用 `IProcessLogRepository`；仓储实现采用 `ProcessLogRepository`。
- **方法名**：动词开头，遵循 `[动词][名词]Async`，如 `LogMistakeProcessAsync`、`AddAsync`、`SaveChangesAsync`。
- **返回值类**：gRPC 层使用 `BoolResponse`；领域层直接返回 `bool`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_processLogRepository`、`_unitOfWork`）。
- **数据库**：表名 `process_log` 小写蛇形；列名 `id`、`mistake_item_id`、`action`、`details`、`created_at`。
- **proto 枚举**：`ProcessAction` 使用 `PROCESS_ACTION_` 前缀 + 大写下划线（如 `PROCESS_ACTION_CREATED`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在操作日志的 `details` 字段中记录学生个人信息、密码、token 等敏感数据。
- **日志级别**：
  - `Information`：日志记录成功时记录摘要（如 "Process log created for mistake {MistakeItemId}"）。
  - `Warning`：错题条目不存在时记录（包含 `MistakeItemId`）。
  - `Error`：事务失败、仓储写入失败。必须包含 `MistakeItemId` 结构化属性。
- **当前实现**：当前领域服务不记录 ILogger 日志；建议添加 Info 级别日志记录操作日志的创建。

## 错误消息格式约定

| 场景 | 消息文本（英文） |
| --- | --- |
| GUID 格式无效 | 由 `Guid.Parse` 抛出 `FormatException` |
| 错题条目不存在 | 不抛异常，返回 `BoolResponse { Success = false }` |

> 注：当前不校验 `action` 枚举值是否为 UNSPECIFIED；不校验 `student_id` 有效性。未来如有需求可添加校验。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（仓储、工作单元）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null`。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `LogMistakeProcess_ShouldCreateLog`。
- **确定性**：时间字段仅做范围断言（如 `±5s`）；GUID 只断言不等于 `Guid.Empty`。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- `ProcessAction` 枚举值在领域服务中使用 `(int)action` 强制转换为整数存储。