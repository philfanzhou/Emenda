# GetMistakeItem — 约定与规范 (CONVENTIONS)

## 命名约定

- **命名空间**：领域代码使用 `Ruoyu.Study.Mistake.Domain.*`；数据库层使用 `Ruoyu.Study.Mistake.Database.*`；服务层使用 `Ruoyu.Study.Mistake.Service`。
- **类名**：领域服务采用 `MistakeDomainService`；仓储接口采用 `IMistakeItemRepository`。
- **方法名**：动词开头，遵循 `[动词][名词][Async]`，如 `GetMistakeItemAsync`、`GetMistakeItemsByUploadAsync`、`GetMistakeItemListAsync`。
- **返回值类**：gRPC 层使用 proto 定义的 `MistakeItemDto`、`MistakeItemListResponse`、`MistakeItemPageResult`；领域层返回 `MistakeItemModel?`、`List<MistakeItemModel>`、`(List<MistakeItemModel>, int)` 元组。
- **私有字段**：采用 `_camelCase` 下划线前缀（如 `_mistakeItemRepository`）。
- **proto 字段**：使用 `snake_case`（如 `source_upload_id`、`review_status`、`page_meta`、`total_count`、`total_pages`）。

## 日志和安全要求

- **禁止记录敏感信息**：不得在日志中记录查询条件（如 student_id）；`MistakeItemId` 仅在错误上下文中记录。
- **日志级别**：
  - `Information`：无需记录（查询为常规操作）。
  - `Warning`：无需记录（条目不存在不视为异常）。
  - `Error`：数据库查询失败。必须包含查询参数的结构化属性以便排查。
- **错误消息**：`GetMistakeItem` 条目不存在时返回 gRPC NotFound 状态码 "错题条目不存在"；其他查询接口不返回错误消息（空结果即正常返回）。

## 错误消息格式约定

| 场景 | 消息文本（中文） | 状态码 |
| --- | --- | --- |
| GetMistakeItem 条目不存在 | `"错题条目不存在"` | `NotFound` |

> 注：`GetMistakeItemsByUpload` 和 `GetMistakeItemList` 的"无结果"场景不抛出异常，正常返回空列表。

## 测试工具要求

- **框架**：`xUnit` 2.x。
- **Mock**：`Moq` 4.x；外部依赖（仓储）必须通过 Mock 替换，不得在测试中访问真实数据库。
- **断言库**：使用 `FluentAssertions`（项目现有风格）。
- **异步**：测试方法必须为 `async Task`，使用 `await` 调用被测代码；禁止 `Task.Wait()` / `.Result`。
- **测试命名**：`[被测方法]_[场景]_[预期行为]`，如 `GetMistakeItemAsync_ShouldReturnItem`、`GetMistakeItemList_ShouldNormalizePageSize`。
- **分页计算验证**：使用整数除法验证 total_pages，避免浮点误差。

## 代码风格

- 遵循项目现有 C# 风格：`PascalCase` 命名空间、类名、方法名、公共字段；`_camelCase` 私有字段。
- 使用文件顶部的 `using` 指令；不要使用 `this.` 前缀访问成员。
- 构造函数注入依赖的顺序应保持与 DI 容器注册顺序一致。
- 异步方法必须返回 `Task` / `Task<T>`，以 `Async` 后缀结尾。
- 分页参数的规范化逻辑统一在领域服务层执行（不在仓储层重复）。
- 非法 page/size 值的处理策略为"静默修正"而非抛异常，确保 API 的健壮性。
- DTO 映射方法 `MapMistakeItemDto` 对所有可空字段使用 `?? string.Empty` 或条件判断，避免 null 引用。
- 时间字段映射规则：`DateTimeOffset.MinValue` → 空字符串；`null` → 空字符串；有效值 → ISO 8601 格式字符串。
- total_pages 计算公式：`(totalCount + size - 1) / size`（整数除法向上取整）。