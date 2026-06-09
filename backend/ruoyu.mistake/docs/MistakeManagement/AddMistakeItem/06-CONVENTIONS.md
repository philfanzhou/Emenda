# AddMistakeItem — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`；服务层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；模型采用 `XxxModel`（如 `MistakeItemModel`、`SourceRegionModel`、`BoundingBoxModel`）；仓储接口采用 `IXxxRepository`；仓储实现采用 `XxxRepository`；异常类采用 `MistakeValidationException`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `AddMistakeItemAsync`、`GetMistakeItemAsync`。
- **返回值类**：gRPC 层使用 proto 定义的响应类型（如 `MistakeItemDto`）；领域层直接返回模型（`MistakeItemModel`）或 `bool`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_unitOfWork`、`_logger`）。
- **proto 字段**：使用 `snake_case`（如 `student_id`、`source_upload_id`、`source_regions`、`review_status`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录完整的 `StudentId` 列表；`MistakeItemId` 仅在错误上下文中记录。
- **日志级别**：
  - `Information`：关键操作，例如错题条目创建成功、操作日志记录成功。
  - `Warning`：非致命错误。
  - `Error`：事务失败、数据库写入失败。必须包含 `MistakeItemId` 结构化属性以便排查。
- **错误消息**：领域服务内部的校验错误消息为中文（如 "缺少学生ID"、"学科值无效"）；gRPC 层 catch 后以 `RpcException(StatusCode.InvalidArgument)` 方式抛出，消息原文透传。

## 错误消息格式约定

| 场景 | 消息文本（中文） |
| --- | --- |
| student_id 为空 | `"缺少学生ID"` |
| subject 超出范围 | `"学科值无效（有效范围：0-9）"` |
| grade 超出范围 | `"年级值无效（有效范围：0-12）"` |
| source_upload_id 为空 | `"缺少上传记录ID"` |
| source_regions 为空 | `"至少需要一个图片区域"` |
| 多条校验错误 | 以分号分隔拼接，如 `"缺少学生ID; 学科值无效（有效范围：0-9）"` |

> 注：领域服务内部 `throw` 原始 `MistakeValidationException`，由 gRPC 层的 try-catch 块统一转换为 `RpcException(StatusCode.InvalidArgument)`。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（仓储、工作单元）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言库**：使用 `FluentAssertions`（项目现有风格）。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `AddMistakeItemAsync_WithSourceRegions_ShouldSucceed`。
- **确定性**：时间字段仅做范围断言（如 `±5s`）；GUID 只断言不等于 `Guid.Empty`。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 参数校验在领域服务层通过 `ValidateMistakeItemData` 静态方法统一进行。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- 操作日志通过私有 `AddProcessLogAsync` 方法封装，确保 Action 和 Details 格式一致。