# 错题类型分析 - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

无。当前功能无任何测试覆盖。

## 测试场景

### TS-01: AnalyzeMistakeTypeAsync_错题存在时返回硬编码值并记录日志

- **Given** IMistakeItemRepository.GetByIdAsync 返回一个 MistakeItemModel
- **When** 调用 MistakeDomainService.AnalyzeMistakeTypeAsync(validId)
- **Then** 返回 MistakeTypeResponse { Type=5, TypeName="其他错误", AnalysisReason="根据答案对比无法明确分类" }
- **And** IProcessLogRepository.AddAsync 被调用一次，Action = (int)ProcessAction.Analyzed
- **And** IUnitOfWork.SaveChangesAsync 被调用一次

```csharp
[Fact]
public async Task AnalyzeMistakeTypeAsync_WhenItemExists_ShouldReturnHardcodedAndLogProcess()
{
    var itemId = Guid.NewGuid();
    var item = new MistakeItemModel { Id = itemId };
    _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync(item);

    var result = await _service.AnalyzeMistakeTypeAsync(itemId);

    result.Type.Should().Be(5);
    result.TypeName.Should().Be("其他错误");
    result.AnalysisReason.Should().Be("根据答案对比无法明确分类");
    _mockLogRepo.Verify(x => x.AddAsync(It.Is<ProcessLogModel>(p =>
        p.MistakeItemId == itemId && p.Action == (int)ProcessAction.Analyzed)), Times.Once);
    _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
}
```

### TS-02: AnalyzeMistakeTypeAsync_错题不存在时返回硬编码值但不记录日志

- **Given** IMistakeItemRepository.GetByIdAsync 返回 null
- **When** 调用 MistakeDomainService.AnalyzeMistakeTypeAsync(validId)
- **Then** 返回 MistakeTypeResponse { Type=5, TypeName="其他错误", AnalysisReason="根据答案对比无法明确分类" }
- **And** IProcessLogRepository.AddAsync 未被调用
- **And** IUnitOfWork.SaveChangesAsync 未被调用

```csharp
[Fact]
public async Task AnalyzeMistakeTypeAsync_WhenItemNotExists_ShouldReturnHardcodedWithoutLogging()
{
    var itemId = Guid.NewGuid();
    _mockItemRepo.Setup(x => x.GetByIdAsync(itemId)).ReturnsAsync((MistakeItemModel?)null);

    var result = await _service.AnalyzeMistakeTypeAsync(itemId);

    result.Type.Should().Be(5);
    result.TypeName.Should().Be("其他错误");
    result.AnalysisReason.Should().Be("根据答案对比无法明确分类");
    _mockLogRepo.Verify(x => x.AddAsync(It.IsAny<ProcessLogModel>()), Times.Never);
    _mockUow.Verify(x => x.SaveChangesAsync(), Times.Never);
}
```

### TS-03: AnalyzeMistakeType_gRPC_无效Guid格式

- **Given** AnalyzeMistakeRequest.MistakeItemId = "not-a-guid"
- **When** 调用 MistakeServiceImpl.AnalyzeMistakeType
- **Then** 抛出 FormatException（Guid.Parse 失败）

### TS-04: AnalyzeMistakeType_gRPC_参数未传递

- **Given** AnalyzeMistakeRequest 包含 content/correct_answer/student_answer
- **When** 调用 MistakeServiceImpl.AnalyzeMistakeType
- **Then** 领域服务仅接收 mistakeItemId，其他参数被忽略

## 遗漏的测试场景

| 场景 | 优先级 | 说明 |
|------|--------|------|
| 错题存在时返回硬编码值 + 记录日志 | P0 | 核心路径无覆盖 |
| 错题不存在时返回硬编码值 + 不记录日志 | P0 | 边界条件无覆盖 |
| gRPC 层 Guid 解析异常 | P1 | 当前无保护 |
| gRPC 层参数未传递验证 | P2 | 已知限制，待 TAT-09 实现后补充 |
| 真正分析逻辑的测试 | P2 | 待 TAT-07 实现后补充 |
