# GetMistakePractices — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`；服务层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；模型采用 `MistakePracticeModel`；仓储接口采用 `IMistakePracticeRepository`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `GetPracticesAsync`、`GetByMistakeItemIdAsync`。
- **返回值类**：gRPC 层返回 proto 定义的 `MistakePracticeListResponse`；领域层返回 `List<MistakePracticeModel>`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakePracticeRepository`）。
- **gRPC 消息**：使用共享的 `IdRequest`（`id` 字段）作为请求，返回类型为 `MistakePracticeListResponse`。

## 日志和安全要求

- **日志级别**：简单查询操作无需额外日志记录。
- **安全**：由上层 API 网关负责鉴权，确保调用者有权访问对应错题的练习记录；不在此层做权限校验。
- **数据暴露**：返回的 `MistakePracticeDto` 包含 `student_answer` 和 `image_paths`，确保 API 网关已在路由层做权限隔离。

## 错误消息格式约定

| 场景 | 消息文本（英文/系统） |
| --- | --- |
| 非法 GUID 格式 | `Guid.Parse` 抛出 `FormatException`（由 gRPC 运行时处理） |

> 注：本功能为纯查询操作，无业务异常路径需要定义中文错误消息。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **断言**：使用 `FluentAssertions` 4.x 进行流畅断言。
- **Mock**：`Moq` 4.x；`IMistakePracticeRepository` 必须通过 Mock 替换，不得在测试中访问真实数据库。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `GetPracticesAsync_ShouldReturnPracticeList`。
- **确定性**：时间字段仅验证非空或格式正确；GUID 只断言为有效值。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 查询方法为简单委托模式：领域服务方法只转发调用到仓储，不做额外业务逻辑。
- gRPC 层的 DTO 映射方法（`MapPracticeDto`）为 `private static`，保持无副作用。
- `PracticeDate` 的 `DateTimeOffset.MinValue` 边界处理：返回空字符串而非最小日期字符串。