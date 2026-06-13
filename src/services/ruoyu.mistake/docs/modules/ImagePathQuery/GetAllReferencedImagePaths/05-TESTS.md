# 图片路径查询 - 测试文档

## 测试工具

- xUnit
- Moq
- FluentAssertions

## 现有测试文件

- `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`
  - `GetAllReferencedImagePathsAsync_ShouldReturnAllPaths` — 验证基本路径收集

### 现有测试详情

```csharp
[Fact]
public async Task GetAllReferencedImagePathsAsync_ShouldReturnAllPaths()
{
    var items = new List<MistakeItemModel>
    {
        new()
        {
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "path1.jpg" },
                new() { SourceImagePath = "path2.jpg" }
            }
        }
    };
    var practices = new List<MistakePracticeModel>
    {
        new()
        {
            ImagePaths = new List<string> { "practice1.jpg" }
        }
    };

    _mockItemRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(items);
    _mockPracticeRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(practices);

    var result = await _service.GetAllReferencedImagePathsAsync();

    result.Should().HaveCount(3);
    result.Should().Contain("path1.jpg");
    result.Should().Contain("path2.jpg");
    result.Should().Contain("practice1.jpg");
}
```

## 测试场景

### TS-01: GetAllReferencedImagePathsAsync_正常返回所有路径 ✅ 已覆盖

- **Given** MistakeItem 有 path1.jpg, path2.jpg；MistakePractice 有 practice1.jpg
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 返回包含 3 个路径的集合

### TS-02: GetAllReferencedImagePathsAsync_大小写不敏感去重

- **Given** MistakeItem 路径为 "Path1.jpg"，MistakePractice 路径为 "path1.JPG"
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 返回 1 个路径（去重后）

```csharp
[Fact]
public async Task GetAllReferencedImagePathsAsync_ShouldDeduplicateCaseInsensitive()
{
    var items = new List<MistakeItemModel>
    {
        new()
        {
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "Path1.jpg" }
            }
        }
    };
    var practices = new List<MistakePracticeModel>
    {
        new() { ImagePaths = new List<string> { "path1.JPG" } }
    };

    _mockItemRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(items);
    _mockPracticeRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(practices);

    var result = await _service.GetAllReferencedImagePathsAsync();

    result.Should().HaveCount(1);
}
```

### TS-03: GetAllReferencedImagePathsAsync_空值和空白字符串过滤

- **Given** SourceRegion.SourceImagePath = "" / "  " / null；MistakePractice.ImagePaths 包含 "" / null
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 空值和空白字符串不出现在结果中

```csharp
[Fact]
public async Task GetAllReferencedImagePathsAsync_ShouldFilterEmptyPaths()
{
    var items = new List<MistakeItemModel>
    {
        new()
        {
            SourceRegions = new List<SourceRegionModel>
            {
                new() { SourceImagePath = "" },
                new() { SourceImagePath = "   " },
                new() { SourceImagePath = null! },
                new() { SourceImagePath = "valid.jpg" }
            }
        }
    };
    var practices = new List<MistakePracticeModel>
    {
        new()
        {
            ImagePaths = new List<string> { "", "practice.jpg" }
        }
    };

    _mockItemRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(items);
    _mockPracticeRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(practices);

    var result = await _service.GetAllReferencedImagePathsAsync();

    result.Should().HaveCount(2);
    result.Should().Contain("valid.jpg");
    result.Should().Contain("practice.jpg");
}
```

### TS-04: GetAllReferencedImagePathsAsync_无图片时返回空集合

- **Given** 无 MistakeItem 和 MistakePractice
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 返回空 HashSet

```csharp
[Fact]
public async Task GetAllReferencedImagePathsAsync_WhenNoData_ShouldReturnEmptySet()
{
    _mockItemRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<MistakeItemModel>());
    _mockPracticeRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<MistakePracticeModel>());

    var result = await _service.GetAllReferencedImagePathsAsync();

    result.Should().BeEmpty();
}
```

## 遗漏的测试场景

| 场景 | 优先级 | 说明 |
|------|--------|------|
| 大小写不敏感去重 | P0 | 核心去重逻辑无覆盖 |
| 空值和空白字符串过滤 | P0 | 过滤逻辑无覆盖 |
| 无图片时返回空集合 | P1 | 边界条件无覆盖 |
| gRPC 层 Empty 请求和响应映射 | P2 | 集成测试 |
