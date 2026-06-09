# ReviewMistakeItem — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`。
- **类名**：领域服务采用 `MistakeDomainService`；模型采用 `XxxModel`；仓储接口采用 `IXxxRepository`；仓储实现采用 `XxxRepository`。
- **方法名**：动词开头，遵循 `[动词][名词]Async`，如 `ReviewMistakeItemAsync`、`GetByIdAsync`、`UpdateAsync`。
- **返回值类**：gRPC 层使用 proto 定义的响应类型（如 `BoolResponse`）；领域层直接返回模型或基础类型。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`、`_processLogRepository`、`_unitOfWork`）。
- **数据库**：错题表 `mistake_item` 小写蛇形；审核相关列：`review_status (int)`、`reviewer_id (text)`、`reviewed_at (timestamptz)`、`review_comment (text)`。
- **审核状态**：使用 `int` 存储，值对应 proto `ReviewStatus` 枚举：1=PendingReview, 2=Confirmed, 3=Rejected。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录完整的审核备注（`review_comment`）内容；`ReviewerId` 仅在结构化属性中记录。
- **日志级别**：
  - `Information`：审核操作成功（确认/驳回），应记录错题 ID 和审核结果。
  - `Warning`：错题不存在、非待审核状态被拒绝。
  - `Error`：事务失败、数据库写入失败。必须包含 `MistakeItemId` 结构化属性以便排查。
- **错误消息**：对外返回的 gRPC 错误消息不应包含内部异常堆栈或数据库细节，应提供面向用户的简短描述（如 "审核结果只能是确认或驳回"、"只能审核待审核状态的错题"）。
- **操作日志**：每次审核操作必须通过 `AddProcessLogAsync` 记录到 `process_log` 表，包含 `ProcessAction.ReviewConfirmed` 或 `ProcessAction.ReviewRejected`。

## 错误消息格式约定

| 场景 | 消息文本（中文） |
| --- | --- |
| 非法审核结果 | `"审核结果只能是确认或驳回"` |
| 错题非待审核状态 | `"只能审核待审核状态的错题"` |

> 注：领域服务内部通过 `MistakeValidationException` 抛出验证异常，由 gRPC 层统一捕获并转换为 `RpcException(StatusCode.InvalidArgument)`。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（仓储、工作单元）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言风格**：使用 xUnit 内置 `Assert.Equal/NotEqual/True/False/NotNull/Null`；避免使用第三方 fluent 断言库以保持项目一致性。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `ReviewMistakeItemAsync_Confirm_ShouldUpdateStatusAndLog`。
- **确定性**：时间字段仅做范围断言（如 `±5s`）。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 审核状态使用整数常量与 proto `ReviewStatus` 枚举值对齐，不要散落魔法数字。
- 领域服务中禁止直接操作 `DbContext` 或执行 SQL；所有数据访问必须通过仓储接口。
- gRPC 层负责前置参数校验（审核结果值合法性），领域服务层负责业务状态校验（错题是否可审核）。