# AddMistakePractice — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`；服务层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；模型采用 `MistakePracticeModel`；仓储接口采用 `IMistakePracticeRepository`；仓储实现采用 `MistakePracticeRepository`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `AddPracticeAsync`、`GetPracticesAsync`。
- **返回值类**：gRPC 层使用 proto 定义的 `MistakePracticeDto`；领域层返回 `MistakePracticeModel`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakePracticeRepository`、`_mistakeItemRepository`、`_unitOfWork`、`_logger`）。
- **数据库**：表名 `mistake_practice` 小写蛇形；列名 `id`、`mistake_item_id`、`student_id`、`practice_date`、`image_paths`（JSONB）、`student_answer`、`is_correct`、`comments`。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录 student_answer 的完整内容；student_id 仅在错误上下文中记录。
- **日志级别**：
  - `Information`：关键操作，例如练习记录创建成功。
  - `Warning`：业务约束触发，例如对非 Confirmed 错题尝试添加练习。
  - `Error`：事务失败、数据库写入失败。必须包含 `MistakeItemId` 结构化属性以便排查。
- **错误消息**：对外返回的 gRPC 错误消息不应包含内部异常堆栈或数据库细节，应提供面向用户的简短中文描述。

## 错误消息格式约定

| 场景 | 消息文本（中文） |
| --- | --- |
| 错题条目不存在 | `"错题条目不存在"` |
| 错题条目未确认 | `"只能对已确认的错题进行练习"` |

> 注：领域服务通过 `MistakeValidationException` 抛出业务校验错误，gRPC 层捕获后转换为 `RpcException(StatusCode.InvalidArgument)`。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **断言**：使用 `FluentAssertions` 4.x 进行流畅断言（如 `result.Should().NotBeNull()`）。
- **Mock**：`Moq` 4.x；外部依赖（仓储、工作单元）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `AddPracticeAsync_WhenItemConfirmed_ShouldAddPractice`。
- **确定性**：时间字段仅做范围断言（如 `±5s`）；GUID 只断言不等于 `Guid.Empty`。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 公共方法在必要时应提供参数 guard。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- 练习记录与操作日志必须在同一事务中通过 `IUnitOfWork.SaveChangesAsync` 提交。