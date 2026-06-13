# 图片路径查询 - 设计文档

## 文件结构

| 文件 | 项目相对路径 | 说明 |
|------|-------------|------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` | RPC: `GetAllReferencedImagePaths(Empty) returns (GetAllReferencedImagePathsResponse)` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` | `Empty`, `GetAllReferencedImagePathsResponse` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` | `GetAllReferencedImagePaths` 方法 |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` | `GetAllReferencedImagePathsAsync()` |
| 仓储接口 | `src/Domain/Repositories/IMistakeItemRepository.cs` | `GetAllSourceImagePathsAsync()` |
| 仓储接口 | `src/Domain/Repositories/IMistakePracticeRepository.cs` | `GetAllPracticeImagePathsAsync()` |
| 仓储实现 | `src/Database/Repositories/MistakeItemRepository.cs` | EF Core 投影查询 |
| 仓储实现 | `src/Database/Repositories/MistakePracticeRepository.cs` | EF Core 投影查询 |
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
Task<List<string>> GetAllSourceImagePathsAsync()

// IMistakePracticeRepository.cs
Task<List<string>> GetAllPracticeImagePathsAsync()
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
  ├── IMistakeItemRepository.GetAllSourceImagePathsAsync()
  │     │  SELECT source_regions FROM mistake_items  (投影查询，只取需要的列)
  │     ▼
  │  遍历每个 region
  │     └── SourceImagePath 非空 → paths.Add(path)
  │
  ├── IMistakePracticeRepository.GetAllPracticeImagePathsAsync()
  │     │  SELECT image_paths FROM mistake_practices  (投影查询，只取需要的列)
  │     ▼
  │  遍历每个 path
  │     └── path 非空 → paths.Add(path)
  │
  ▼
HashSet<string> (StringComparer.OrdinalIgnoreCase)
  │
  ▼
GetAllReferencedImagePathsResponse { image_paths: [...] }
```

## 关键设计决策

1. **投影查询替代全表扫描**: 使用 `GetAllSourceImagePathsAsync()` / `GetAllPracticeImagePathsAsync()` 只查询 `source_regions` / `image_paths` 列，避免加载完整实体到内存。数据库层过滤空值路径。
2. **大小写不敏感去重**: 使用 `HashSet<string>(StringComparer.OrdinalIgnoreCase)` 确保不同大小写的相同路径只保留一个。
3. **返回 HashSet**: 领域服务返回 `HashSet<string>` 而非 `List<string>`，语义上表达去重结果，gRPC 层直接 `AddRange` 到 repeated 字段。
4. **无参数设计**: 不需要任何输入参数，查询全量引用路径，适用于外部系统批量比对场景（如 OssAuditWorker 僵尸对象检测）。
