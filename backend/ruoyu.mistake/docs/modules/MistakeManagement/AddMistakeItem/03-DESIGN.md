# AddMistakeItem - 设计文档

## 文件结构

| 文件 | 项目相对路径 |
|------|-------------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` |
| 领域模型 | `src/Domain/Models/MistakeItemModel.cs` |
| 仓储接口 | `src/Domain/Repositories/IMistakeItemRepository.cs` |
| 仓储实现 | `src/Database/Repositories/MistakeItemRepository.cs` |
| 数据实体 | `src/Database/Entities/MistakeItemEntity.cs` |
| 数据上下文 | `src/Database/MistakeDbContext.cs` |
| 单元测试 | `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs` |

## 关键接口签名

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<MistakeItemDto> AddMistakeItem(AddMistakeItemRequest request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<MistakeItemModel> AddMistakeItemAsync(MistakeItemModel item)
```

### 仓储接口

```csharp
// IMistakeItemRepository.cs
Task AddAsync(MistakeItemModel model);
```

### 验证方法

```csharp
// MistakeDomainService.cs (private)
private static void ValidateMistakeItemData(MistakeItemModel item)
```

## 依赖的数据库表

| 表名 | 说明 |
|------|------|
| `mistake_item` | 错题条目主表，存储错题基本信息及 JSONB 格式的 source_regions |
| `process_log` | 操作日志表，记录错题生命周期事件 |

> 数据库 Schema 详情参见 `src/Database/MistakeDbContext.cs` 中的 `OnModelCreating` 配置。

## 数据流 / 调用链

```
Client
  │
  ▼
MistakeServiceImpl.AddMistakeItem(request)
  │  1. 构造 MistakeItemModel（映射 SourceRegion/BoundingBox）
  │
  ▼
MistakeDomainService.AddMistakeItemAsync(item)
  │  2. ValidateMistakeItemData(item)
  │     ├─ studentId 非空
  │     ├─ subject 1-9
  │     ├─ grade 0-12
  │     ├─ sourceUploadId 非空
  │     └─ sourceRegions 非空
  │  3. item.Id = Guid.NewGuid()
  │  4. item.ReviewStatus = PendingReview
  │  5. item.CreatedAt = UtcNow
  │
  ▼
IMistakeItemRepository.AddAsync(item)
  │  6. Model → Entity 映射
  │  7. DbContext.MistakeItems.AddAsync(entity)
  │
  ▼
AddProcessLogAsync(item.Id, Created, "Mistake item manually added by teacher")
  │  8. 创建 ProcessLogModel
  │  9. IProcessLogRepository.AddAsync(log)
  │
  ▼
IUnitOfWork.SaveChangesAsync()
  │  10. 提交事务
  │
  ▼
MistakeServiceImpl: MapMistakeItemDto(created) → MistakeItemDto
  │
  ▼
Client ← MistakeItemDto
```

## 关键设计决策

1. **验证在领域层执行**：`ValidateMistakeItemData` 在 `MistakeDomainService` 中实现，gRPC 层仅做 proto→model 映射，保证领域逻辑内聚。
2. **验证范围差异**：`AddMistakeItem` 的 subject 范围为 1-9（不允许 0），与 `SubmitMistakeUpload` 一致，手动添加场景同样要求指定有效学科。
3. **ProcessLog 与主表在同一事务**：通过 `IUnitOfWork.SaveChangesAsync()` 统一提交，保证数据一致性。
4. **SourceRegions 存储为 JSONB**：使用 PostgreSQL 的 jsonb 类型存储，支持灵活的区域数据结构。
