# GetAllReferencedImagePaths — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；gRPC 层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；响应类型使用 proto 定义的 `GetAllReferencedImagePathsResponse`。
- **方法名**：动词开头，遵循 `[动词][名词]Async`，如 `GetAllReferencedImagePathsAsync`。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_mistakePracticeRepository`）。
- **proto 字段**：`GetAllReferencedImagePathsResponse` 使用 snake_case（如 `image_paths`）。

## 日志和安全要求

- **禁止记录敏感信息**：查询操作不记录日志；不记录返回的完整路径列表。
- **日志级别**：当前为只读查询，不记录日志。
- **安全**：此接口返回所有引用路径，不应暴露给普通用户，仅限管理员/运维人员访问（由上层 API 网关负责鉴权）。
- **路径格式**：返回的路径为 OSS 对象键（如 `mistakes/2024/01/abc.jpg`），不包含 OSS endpoint 前缀。

## 错误消息格式约定

| 场景 | 消息文本（英文） |
| --- | --- |
| 仓储异常 | 异常由 gRPC 框架转为 Internal 错误 |
| 空数据集 | 返回空的 `image_paths` 列表（不抛异常） |

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；`IMistakeItemRepository`、`IMistakePracticeRepository` 必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/Contains/Single`。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `GetAllReferencedImagePaths_ShouldCollectAllPaths`。
- **集合断言**：使用 `Assert.Contains` / `Assert.DoesNotContain` 验证路径是否在结果集中。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 禁止在领域服务中暴露 `DbContext` 或直接执行 SQL；所有数据访问必须通过仓储接口。
- 去重使用 `HashSet<string>(StringComparer.OrdinalIgnoreCase)`，**不**使用 `CurrentCultureIgnoreCase`（避免不同区域设置下的行为差异）。
- 空白路径过滤使用 `string.IsNullOrWhiteSpace`。