# CompleteUploadReview — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`。
- **类名**：领域结果模型采用 `CompleteUploadReviewResult`；服务采用 `MistakeServiceImpl`。
- **方法名**：动词开头，遵循 `[动词][名词]Async`，如 `CompleteUploadReviewAsync`、`CopyObjectAsync`、`DeleteAsync`。
- **返回值类**：gRPC 层使用 proto 定义的响应类型 `CompleteUploadReviewResponse`；领域层使用 `CompleteUploadReviewResult`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_ossService`、`_studentClient`、`_mistakeService`、`_logger`）。
- **OSS 路径约定**：临时上传目录前缀 `uploads/`；正式错题目录前缀 `mistakes/`；路径转换规则为 `mistakes + oldPath.Substring("uploads".Length)`。
- **gRPC 客户端命名**：`_studentClient`（类型 `StudentLearningGrpcServiceClient`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录完整的 OSS 路径参数列表；`SourceUploadId` 和 `StudentId` 仅在结构化属性中记录。
- **日志级别**：
  - `Information`：每次成功的图片迁移，格式为 `"Migrated image from {OldPath} to {NewPath}"`；跳过迁移的驳回错题，格式为 `"Item {ItemId} is not Confirmed (status={Status}), will only remove from upload record, not migrate"`。
  - `Warning`：非预期但可恢复的错误。
  - `Error`：单张图片 OSS 迁移失败，格式为 `"Error migrating image {Path}"`；Student 服务调用失败，格式为 `"Error removing images from upload record {RecordId}"`。
- **错误消息**：对外返回的错误消息不应包含内部异常堆栈或 OSS 细节，应提供面向用户的简短描述。

## 错误消息格式约定

| 场景 | 消息文本（中文） |
| --- | --- |
| 存在待审核错题 | `"还有 {pendingCount} 条错题待审核，请先完成所有审核"` |
| 上传记录不存在 | `"未找到该上传记录的错题"` |

> 注：gRPC 层通过 `CompleteUploadReviewResponse` 返回错误消息，不抛 RpcException（语义更明确）。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（OSS 服务、Student gRPC 客户端、领域服务、仓储）必须通过 Mock 替换，不得在测试中访问真实 OSS 或真实 Student 服务。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null`；避免使用第三方 fluent 断言库以保持项目一致性。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `CompleteUploadReviewAsync_HasPending_ReturnsFalse`。
- **确定性**：Mock 所有外部依赖，确保测试无不确定性。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- OSS 操作使用 `await` 逐一执行（非并发），确保顺序可预期；在部分失败场景下保证已成功图片的持久化。
- gRPC 层负责编排 OSS 操作和跨服务调用；领域服务仅负责数据校验和返回原始数据。
- 路径映射缓存使用 `Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)` 确保大小写不敏感的路径匹配。
- 图片路径去重收集使用 `List<string>` 配合 `Contains(StringComparer.OrdinalIgnoreCase)` 确保大小写不敏感的去重。