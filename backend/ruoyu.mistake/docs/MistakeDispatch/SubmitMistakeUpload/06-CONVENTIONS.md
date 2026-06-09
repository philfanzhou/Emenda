# SubmitMistakeUpload — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`；服务层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；模型采用 `MistakeItemModel`、`SourceRegionModel`、`BoundingBoxModel`；仓储接口采用 `IMistakeItemRepository`、`IProcessLogRepository`；仓储实现采用 `MistakeItemRepository`、`ProcessLogRepository`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `SubmitMistakeUploadAsync`、`GetMistakeItemAsync`。
- **返回值类**：gRPC 层使用 proto 定义的响应类型（如 `SubmitMistakeUploadResponse`）；领域层返回 `List<MistakeItemModel>`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_unitOfWork`、`_logger`）。
- **数据库**：表名 `mistake_item` 小写蛇形；列名 `id`、`student_id`、`subject`、`grade`、`source_upload_id`、`source_regions`（JSONB）、`review_status`、`created_at`、`updated_at`。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录完整的 image_paths 列表；student_id 仅在错误上下文中记录。
- **日志级别**：
  - `Information`：关键操作，例如错题条目创建成功。
  - `Warning`：非致命错误，例如部分参数校验失败但可降级。
  - `Error`：事务失败、数据库写入失败。必须包含 `StudentId`、`SourceUploadId` 结构化属性以便排查。
- **错误消息**：对外返回的 gRPC 错误消息不应包含内部异常堆栈或数据库细节，应提供面向用户的简短中文描述。

## 错误消息格式约定

| 场景 | 消息文本（中文） |
| --- | --- |
| 学生 ID 为空 | `"缺少学生ID"` |
| 学科值无效 | `"学科值无效（有效范围：1-9）"` |
| 年级值无效 | `"年级值无效（有效范围：1-12）"` |
| 上传记录 ID 为空 | `"缺少上传记录ID"` |
| 图片路径为空 | `"至少需要提供一张图片"` |

> 注：领域服务通过 `MistakeValidationException` 抛出聚合后的校验错误（以 `; ` 分隔多条错误），gRPC 层捕获后转换为 `RpcException(StatusCode.InvalidArgument)`。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **断言**：使用 `FluentAssertions` 4.x 进行流畅断言（如 `items.Should().HaveCount(1)`）。
- **Mock**：`Moq` 4.x；外部依赖（仓储、工作单元）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `SubmitMistakeUploadAsync_ShouldCreateItemWithSourceRegions`。
- **确定性**：时间字段仅做范围断言；GUID 只断言不等于 `Guid.Empty`。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 公共方法在必要时应提供参数 guard；校验逻辑使用独立的 `static` 方法（如 `ValidateSubmitData`）以保持可测试性。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- 数据库并发字段 `updated_at` 在 Entity 上标记 `[ConcurrencyCheck]`，以保证乐观并发检测生效。
- gRPC 层校验与领域层校验保持一致的错误消息文本。