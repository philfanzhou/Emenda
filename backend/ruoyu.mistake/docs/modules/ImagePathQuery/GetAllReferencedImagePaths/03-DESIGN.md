# 图片路径查询 - 设计文档

## 文件结构

| 文件 | 项目相对路径 | 说明 |
|------|-------------|------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` | RPC: `GetAllReferencedImagePaths(Empty) returns (GetAllReferencedImagePathsResponse)` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` | `Empty`, `GetAllReferencedImagePathsResponse` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` | `GetAllReferencedImagePaths` 方法 |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` | `GetAllReferencedImagePathsAsync()` |
| 仓储接口 | `src/Domain/Repositories/IMistakeItemRepository.cs` | `GetAllAsync()` |
| 仓储接口 | `src/Domain/Repositories/IMistakePracticeRepository.cs` | `GetAllAsync()` |
| 仓储实现 | `src/Database/Repositories/MistakeItemRepository.cs` | EF Core ToListAsync |
| 仓储实现 | `src/Database/Repositories/MistakePracticeRepository.cs` | EF Core ToListAsync |
| 领域模型 | `src/Domain/Models/MistakeItemModel.cs` | `SourceRegionModel.SourceImagePath` |
| 领域模型 | `src/Domain/Models/MistakePracticeModel.cs` | `ImagePaths: List<string>` |

## 关键接口签名

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<GetAllReferencedImagePathsResponse> GetAllReferencedImagePaths(Empty request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<HashSet<string>> GetAllReferencedImagePathsAsync()
```

### 仓储接口

```csharp
// IMistakeItemRepository.cs
Task<List<MistakeItemModel>> GetAllAsync()

// IMistakePracticeRepository.cs
Task<List<MistakePracticeModel>> GetAllAsync()
```

## 依赖的数据库表

| 表名 | 关键字段 | 说明 |
|------|---------|------|
| `mistake_items` | `source_regions` (JSON) | 包含 SourceImagePath |
| `mistake_practices` | `image_paths` (JSON/list) | 包含图片路径列表 |

## 数据流/调用链

```
Client
  │
  ▼
MistakeServiceImpl.GetAllReferencedImagePaths(Empty)
  │
  ▼
MistakeDomainService.GetAllReferencedImagePathsAsync()
  │
  ├── IMistakeItemRepository.GetAllAsync()
  │     │  SELECT * FROM mistake_items
  │     ▼
  │  遍历每个 item.SourceRegions
  │     ├── region.SourceImagePath 非空 → paths.Add(path)
  │     └── region.SourceImagePath 为空/空白 → 跳过
  │
  ├── IMistakePracticeRepository.GetAllAsync()
  │     │  SELECT * FROM mistake_practices
  │     ▼
  │  遍历每个 practice.ImagePaths
  │     ├── path 非空 → paths.Add(path)
  │     └── path 为空/空白 → 跳过
  │
  ▼
HashSet<string> (StringComparer.OrdinalIgnoreCase)
  │
  ▼
GetAllReferencedImagePathsResponse { image_paths: [...] }
```

## 关键设计决策

1. **全表扫描**: 使用 `GetAllAsync()` 加载所有 MistakeItem 和 MistakePractice，在内存中提取路径。适用于数据量有限的场景，大数据量时需优化为数据库层聚合。
2. **大小写不敏感去重**: 使用 `HashSet<string>(StringComparer.OrdinalIgnoreCase)` 确保不同大小写的相同路径只保留一个。
3. **返回 HashSet**: 领域服务返回 `HashSet<string>` 而非 `List<string>`，语义上表达去重结果，gRPC 层直接 `AddRange` 到 repeated 字段。
4. **无参数设计**: 不需要任何输入参数，查询全量引用路径，适用于外部系统批量比对场景。
5. **空值过滤**: 使用 `string.IsNullOrWhiteSpace()` 过滤空值和空白字符串，避免无效路径进入结果。
