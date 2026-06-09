# DeleteMistakeItem — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`；服务层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；仓储接口采用 `IXxxRepository`；仓储实现采用 `XxxRepository`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `DeleteMistakeItemAsync`、`GetByIdAsync`、`RemoveByMistakeItemIdAsync`。
- **返回值类**：gRPC 层使用 proto 定义的 `BoolResponse`；领域层返回 `bool`（true 表示成功，false 表示条目不存在）。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_mistakePracticeRepository`、`_processLogRepository`、`_unitOfWork`）。
- **proto 字段**：`IdRequest` 使用 `snake_case`（`id`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录被删除条目的完整内容；`MistakeItemId` 仅在错误上下文中记录。
- **日志级别**：
  - `Information`：关键操作，如删除成功。
  - `Warning`：条目不存在（非错误，是正常业务逻辑）。
  - `Error`：事务失败、数据库操作失败。必须包含 `MistakeItemId` 结构化属性以便排查。
- **错误消息**：删除失败不抛异常，通过 `BoolResponse.Success` 字段告知调用方；仅在 `MistakeValidationException` 时抛出 InvalidArgument。

## 错误消息格式约定

| 场景 | 消息文本 | 传递方式 |
| --- | --- | --- |
| 条目不存在 | 无消息 | `BoolResponse { Success = false }` |
| 删除成功 | 无消息 | `BoolResponse { Success = true }` |
| 校验异常 | 透传 ex.Message（中文） | `RpcException(StatusCode.InvalidArgument)` |

> 注：删除操作正常路径不抛异常，通过 `BoolResponse.Success` 传递结果。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（仓储、工作单元）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言库**：使用 `FluentAssertions`（项目现有风格）。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `DeleteMistakeItemAsync_WhenConfirmed_ShouldDelete`。
- **调用顺序验证**：使用 Moq `Verify` 的 `Times.Once` 和调用顺序验证确保三级删除顺序正确。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致（item repo → practice repo → log repo → uow → logger）。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- 删除操作为物理删除（非软删除），调用方应确保删除前不再需要该数据。
- 三级删除顺序固定：先子表（练习记录 → 操作日志）后主表（错题条目），符合外键约束逻辑。