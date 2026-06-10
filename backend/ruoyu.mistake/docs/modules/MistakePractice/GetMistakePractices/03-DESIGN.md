# GetMistakePractices - 设计文档

## 文件结构

| 文件 | 项目相对路径 |
|------|-------------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` |
| 领域模型 | `src/Domain/Models/MistakePracticeModel.cs` |
| 仓储接口 | `src/Domain/Repositories/IMistakePracticeRepository.cs` |
| 仓储实现 | `src/Database/Repositories/MistakePracticeRepository.cs` |
| 数据实体 | `src/Database/Entities/MistakePracticeEntity.cs` |

## 关键接口签名

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<MistakePracticeListResponse> GetMistakePractices(IdRequest request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<List<MistakePracticeModel>> GetPracticesAsync(Guid mistakeItemId)
```

### 仓储接口

```csharp
// IMistakePracticeRepository.cs
Task<List<MistakePracticeModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
```

## 依赖的数据库表

| 表名 | 说明 |
|------|------|
| `mistake_practice` | 练习记录表，按 mistake_item_id 查询，按 practice_date 升序排列 |

> 数据库 Schema 详情参见 `src/Database/MistakeDbContext.cs`。mistake_practice 表有 `MistakeItemId` 和 `PracticeDate` 索引。

## 数据流 / 调用链

```
Client
  │
  ▼
MistakeServiceImpl.GetMistakePractices(request)
  │  1. Guid.Parse(request.Id)
  │
  ▼
MistakeDomainService.GetPracticesAsync(Guid.Parse(request.Id))
  │  2. IMistakePracticeRepository.GetByMistakeItemIdAsync(mistakeItemId)
  │     ├─ DbContext.MistakePractices.Where(p => p.MistakeItemId == id)
  │     ├─ .OrderBy(p => p.PracticeDate)
  │     └─ Entity → Model 映射
  │
  ▼
MistakeServiceImpl:
  │  3. practices.Select(MapPracticeDto)
  │  4. 构造 MistakePracticeListResponse
  │
  ▼
Client ← MistakePracticeListResponse
```

## 关键设计决策

1. **简单查询无验证**：不验证错题是否存在，直接查询练习记录，不存在时返回空列表。
2. **排序在数据库层执行**：通过 `OrderBy(p => p.PracticeDate)` 在 SQL 层排序，避免内存排序。
3. **无分页设计**：当前业务场景下单条错题的练习记录数量有限，暂不分页。
4. **领域服务为薄封装**：`GetPracticesAsync` 直接委托给仓储，无额外业务逻辑。
