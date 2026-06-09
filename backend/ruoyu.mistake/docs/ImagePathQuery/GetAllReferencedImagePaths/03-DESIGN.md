# GetAllReferencedImagePaths — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── MistakeItemModel.cs              # 错题条目领域模型（含 SourceRegions）
│   │   │   └── MistakePracticeModel.cs          # 错题练习领域模型（含 ImagePaths）
│   │   ├── Services/
│   │   │   └── MistakeDomainService.cs          # 领域服务 (GetAllReferencedImagePathsAsync)
│   │   └── Repositories/
│   │       ├── IMistakeItemRepository.cs        # 错题条目仓储接口 (GetAllAsync)
│   │       └── IMistakePracticeRepository.cs    # 错题练习仓储接口 (GetAllAsync)
│   ├── Service/
│   │   └── MistakeServiceImpl.cs               # gRPC 实现 (GetAllReferencedImagePaths)
│   └── Contract/Protos/
│       ├── mistake.proto                       # gRPC 契约定义
│       └── mistake.common.proto                # 公共消息定义 (Empty)
└── docs/ImagePathQuery/GetAllReferencedImagePaths/ # 本文档所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 关键接口签名和数据结构定义

### 领域模型相关字段

```csharp
// src/Domain/Models/MistakeItemModel.cs
public class MistakeItemModel
{
    public List<SourceRegionModel> SourceRegions { get; set; } = new();
    // ...
}

public class SourceRegionModel
{
    public string SourceImagePath { get; set; } = string.Empty;
    public BoundingBoxModel? BoundingBox { get; set; }
}

// src/Domain/Models/MistakePracticeModel.cs
public class MistakePracticeModel
{
    public List<string> ImagePaths { get; set; } = new();
    // ...
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs
public async Task<HashSet<string>> GetAllReferencedImagePathsAsync()
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto
rpc GetAllReferencedImagePaths(Empty) returns (GetAllReferencedImagePathsResponse);

// src/Contract/Protos/mistake.common.proto
message Empty {}

message GetAllReferencedImagePathsResponse {
  repeated string image_paths = 1;
}
```

### gRPC 层实现

```csharp
// src/Service/MistakeServiceImpl.cs
public override async Task<GetAllReferencedImagePathsResponse> GetAllReferencedImagePaths(
    Empty request, ServerCallContext context)
{
    var paths = await _mistakeService.GetAllReferencedImagePathsAsync();
    var response = new GetAllReferencedImagePathsResponse();
    response.ImagePaths.AddRange(paths);
    return response;
}
```

### 仓储接口

```csharp
// src/Domain/Repositories/IMistakeItemRepository.cs
public interface IMistakeItemRepository
{
    Task<List<MistakeItemModel>> GetAllAsync();
    // ...
}

// src/Domain/Repositories/IMistakePracticeRepository.cs
public interface IMistakePracticeRepository
{
    Task<List<MistakePracticeModel>> GetAllAsync();
    // ...
}
```

## 数据流描述（步骤序列）

### 查询所有引用图片路径流程 (`GetAllReferencedImagePaths`)

1. gRPC 层接收空的 `Empty` 请求。
2. 调用 `MistakeDomainService.GetAllReferencedImagePathsAsync()`。
3. 领域服务创建 `HashSet<string>(StringComparer.OrdinalIgnoreCase)` 用于去重。
4. 调用 `_mistakeItemRepository.GetAllAsync()` 获取所有错题条目。
5. 遍历每个 `MistakeItem` 的 `SourceRegions`：
   - 若 `region.SourceImagePath` 非空白，则加入 `HashSet`。
6. 调用 `_mistakePracticeRepository.GetAllAsync()` 获取所有错题练习。
7. 遍历每个 `MistakePractice` 的 `ImagePaths`：
   - 若路径非空白，则加入 `HashSet`。
8. 返回去重后的 `HashSet<string>`。
9. gRPC 层将 `HashSet` 添加到 `GetAllReferencedImagePathsResponse.ImagePaths` 并返回。

## 错误处理策略

- **仓储异常**：`GetAllAsync` 异常向上抛出，由 gRPC 框架处理。
- **空路径过滤**：`string.IsNullOrWhiteSpace` 过滤空白路径，不加入结果集。
- **大小写去重**：使用 `StringComparer.OrdinalIgnoreCase` 确保 "a/b.jpg" 和 "A/B.jpg" 视为重复。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 获取所有错题条目 `GetAllAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |
| `IMistakePracticeRepository` | 获取所有错题练习 `GetAllAsync` | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository`、`IMistakePracticeRepository` 全部通过构造函数注入，可被 Moq 替换。
- **去重逻辑可验证**：Mock 返回包含重复路径的数据，验证结果集中无重复。
- **空路径过滤可验证**：Mock 返回包含空白路径的数据，验证空白路径被过滤。
- **跨来源去重可验证**：Mock 返回同一路径同时出现在错题条目和错题练习中，验证结果中仅出现一次。