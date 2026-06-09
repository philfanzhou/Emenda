# UpdateMistakeItem — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`；服务层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；模型采用 `MistakeItemModel`、`SourceRegionModel`、`BoundingBoxModel`；仓储接口采用 `IMistakeItemRepository`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `UpdateMistakeItemAsync`、`GetMistakeItemAsync`。
- **返回值类**：gRPC 层使用 proto 定义的 `MistakeItemDto`；领域层 `UpdateMistakeItemAsync` 返回 `bool`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_unitOfWork`）。
- **proto 字段**：使用 `snake_case`（如 `student_id`、`source_regions`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录完整的字段更新内容。
- **日志级别**：
  - `Information`：关键操作（如更新成功）。
  - `Warning`：非致命错误。
  - `Error`：数据库写入失败。必须包含 `MistakeItemId` 结构化属性以便排查。
- **错误消息**：对外返回的 gRPC 错误消息为简短中文描述（如 "错题条目不存在"、"更新失败"）。

## 错误消息格式约定

| 场景 | 消息文本（中文） |
| --- | --- |
| 条目不存在 | `"错题条目不存在"` |
| 更新失败 | `"更新失败"` |
| 校验异常（MistakeValidationException） | 透传 ex.Message（中文） |

> 注：更新操作不做参数校验（与 AddMistakeItem 不同），校验异常仅由领域服务内部逻辑触发。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（仓储、工作单元）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言库**：使用 `FluentAssertions`（项目现有风格）。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `UpdateMistakeItemAsync_ShouldUpdateItem`。
- **确定性**：GUID 只断言不等于 `Guid.Empty`；不依赖时间戳做精确相等断言。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 部分更新逻辑（字段非空/非零判断）在 gRPC 层实现，领域服务层仅负责持久化。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- 更新后重新查询获取最新数据的模式（Read-After-Write）确保返回值与数据库状态一致。