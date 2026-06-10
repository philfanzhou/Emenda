# 错题处理历史查询 - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

无。当前功能无任何测试覆盖。

## 测试场景

### TS-01: GetMistakeProcessHistoryAsync_正常返回日志列表

- **Given** IProcessLogRepository.GetByMistakeItemIdAsync 返回 3 条 ProcessLogModel
- **When** 调用 MistakeDomainService.GetMistakeProcessHistoryAsync(validId)
- **Then** 返回包含 3 条记录的列表，且委托仓储正确调用

```csharp
[Fact]
public async Task GetMistakeProcessHistoryAsync_ShouldReturnLogs()
{
    var itemId = Guid.NewGuid();
    var logs = new List<ProcessLogModel>
    {
        new() { Id = Guid.NewGuid(), MistakeItemId = itemId, Action = 1, Details = "Created", CreatedAt = DateTimeOffset.UtcNow },
        new() { Id = Guid.NewGuid(), MistakeItemId = itemId, Action = 2, Details = "Analyzed", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1) }
    };

    _mockLogRepo.Setup(x => x.GetByMistakeItemIdAsync(itemId)).ReturnsAsync(logs);

    var result = await _service.GetMistakeProcessHistoryAsync(itemId);

    result.Should().HaveCount(2);
    _mockLogRepo.Verify(x => x.GetByMistakeItemIdAsync(itemId), Times.Once);
}
```

### TS-02: GetMistakeProcessHistoryAsync_无日志返回空列表

- **Given** IProcessLogRepository.GetByMistakeItemIdAsync 返回空列表
- **When** 调用 MistakeDomainService.GetMistakeProcessHistoryAsync(validId)
- **Then** 返回空列表，不抛异常

```csharp
[Fact]
public async Task GetMistakeProcessHistoryAsync_WhenNoLogs_ShouldReturnEmptyList()
{
    var itemId = Guid.NewGuid();
    _mockLogRepo.Setup(x => x.GetByMistakeItemIdAsync(itemId)).ReturnsAsync(new List<ProcessLogModel>());

    var result = await _service.GetMistakeProcessHistoryAsync(itemId);

    result.Should().BeEmpty();
}
```

### TS-03: GetMistakeProcessHistory_gRPC_无效Guid格式

- **Given** IdRequest.Id = "not-a-guid"
- **When** 调用 MistakeServiceImpl.GetMistakeProcessHistory
- **Then** 抛出 FormatException（Guid.Parse 失败）

### TS-04: GetMistakeProcessHistory_gRPC_枚举映射正确

- **Given** ProcessLogModel.Action = 2 (Analyzed)
- **When** MapProcessLogDto 映射
- **Then** ProcessLogDto.Action = ProcessAction.Analyzed

### TS-05: ProcessLogRepository_排序验证

- **Given** 数据库中存在多条日志，CreatedAt 无序
- **When** 调用 GetByMistakeItemIdAsync
- **Then** 返回结果按 CreatedAt 升序排列

## 遗漏的测试场景

| 场景 | 优先级 | 说明 |
|------|--------|------|
| 领域服务正常返回 | P0 | 核心路径无覆盖 |
| 领域服务空结果 | P0 | 边界条件无覆盖 |
| gRPC 层 Guid 解析异常 | P1 | 当前无保护，需验证行为 |
| gRPC 层枚举映射 | P1 | MapProcessLogDto 的 Action 映射 |
| 仓储层排序 | P1 | 验证 OrderBy CreatedAt 升序 |
