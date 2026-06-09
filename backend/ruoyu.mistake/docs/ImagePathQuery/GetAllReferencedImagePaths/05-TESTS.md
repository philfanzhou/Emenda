# GetAllReferencedImagePaths — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：`test/Domain/Services/MistakeDomainServiceTests.cs`。

## 单元测试 — Given-When-Then 格式

### UT-01 收集所有引用路径成功（验证 SPEC FR-01/FR-02）

- **Given**：mock `_mistakeItemRepository.GetAllAsync` 返回 2 条 `MistakeItemModel`，SourceRegions 分别包含路径 "a/b.jpg" 和 "c/d.png"；mock `_mistakePracticeRepository.GetAllAsync` 返回 1 条 `MistakePracticeModel`，ImagePaths 包含 "e/f.jpg"。
- **When**：调用 `MistakeDomainService.GetAllReferencedImagePathsAsync()`。
- **Then**：返回的 `HashSet<string>` 包含 "a/b.jpg"、"c/d.png"、"e/f.jpg"，共 3 个路径；`_mistakeItemRepository.GetAllAsync` 被调用一次；`_mistakePracticeRepository.GetAllAsync` 被调用一次。

### UT-02 去重 — 大小写不同（验证 SPEC FR-03）

- **Given**：mock 返回路径 "a/b.jpg" 和 "A/B.JPG"（同一个 MistakeItem 中）。
- **When**：调用 `GetAllReferencedImagePathsAsync()`。
- **Then**：返回的 `HashSet` 中路径数量为 1（去重成功），大小写不敏感比较生效。

### UT-03 跨来源去重（验证 SPEC FR-03）

- **Given**：mock `MistakeItem` 的 `SourceRegions` 包含 "a/b.jpg"；mock `MistakePractice` 的 `ImagePaths` 也包含 "a/b.jpg"。
- **When**：调用 `GetAllReferencedImagePathsAsync()`。
- **Then**：返回的 `HashSet` 中路径数量为 1（跨来源去重成功）。

### UT-04 过滤空白路径（验证 SPEC FR-04）

- **Given**：mock `MistakeItem` 的 `SourceRegions` 包含 ""（空字符串）和 "  "（空白字符串）和 "valid.jpg"。
- **When**：调用 `GetAllReferencedImagePathsAsync()`。
- **Then**：返回的 `HashSet` 中仅包含 "valid.jpg"，空白路径被过滤。

### UT-05 空数据集（验证 SPEC FR-01/FR-02）

- **Given**：mock `_mistakeItemRepository.GetAllAsync` 返回空列表；mock `_mistakePracticeRepository.GetAllAsync` 返回空列表。
- **When**：调用 `GetAllReferencedImagePathsAsync()`。
- **Then**：返回空的 `HashSet<string>`。

## 集成测试

### IT-01 端到端查询完整路径

1. 使用真实的测试数据库上下文。
2. 先创建几条错题条目（含 SourceRegions 中的路径）和错题练习（含 ImagePaths 中的路径）。
3. 调用 `GetAllReferencedImagePaths` gRPC 方法。
4. 断言返回的路径列表包含所有已写入的路径，且无重复。

## 边界和异常测试

- EX-01 路径为 null → `IsNullOrWhiteSpace` 过滤，不加入结果。
- EX-02 大量路径 → 验证 `HashSet` 在大量数据下的去重性能和内存使用。
- EX-03 仓储异常 → 异常向上传播。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `GetAllReferencedImagePaths_ShouldCollectAllPaths` | FR-01, FR-02 |
| `GetAllReferencedImagePaths_ShouldDeduplicateCaseInsensitive` | FR-03 |
| `GetAllReferencedImagePaths_ShouldDeduplicateCrossSource` | FR-03 |
| `GetAllReferencedImagePaths_ShouldFilterEmpty` | FR-04 |