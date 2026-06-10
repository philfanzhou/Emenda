# UpdateMistakeItem - 设计文档

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
| 单元测试 | `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs` |

## 关键接口签名

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<MistakeItemDto> UpdateMistakeItem(UpdateMistakeItemRequest request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<bool> UpdateMistakeItemAsync(MistakeItemModel model)
```

### 仓储接口

```csharp
// IMistakeItemRepository.cs
Task<MistakeItemModel?> GetByIdAsync(Guid id);
Task<bool> UpdateAsync(MistakeItemModel model);
```

## 依赖的数据库表

| 表名 | 说明 |
|------|------|
| `mistake_item` | 错题条目主表，更新操作的目标表 |

> 数据库 Schema 详情参见 `src/Database/MistakeDbContext.cs` 中的 `OnModelCreating` 配置。

## 数据流 / 调用链

```
Client
  │
  ▼
MistakeServiceImpl.UpdateMistakeItem(request)
  │  1. GetMistakeItemAsync(Guid.Parse(request.Id))
  │     └─ null → RpcException(NotFound, "错题条目不存在")
  │
  │  2. 部分更新逻辑（gRPC 层）:
  │     ├─ studentId 非空 → item.StudentId = request.StudentId
  │     ├─ subject > 0   → item.Subject = request.Subject
  │     ├─ grade > 0     → item.Grade = request.Grade
  │     └─ sourceRegions.Count > 0 → item.SourceRegions = 映射后的列表
  │
  ▼
MistakeDomainService.UpdateMistakeItemAsync(item)
  │  3. IMistakeItemRepository.UpdateAsync(model)
  │     ├─ 查找 Entity
  │     ├─ 逐字段赋值
  │     └─ DbContext.Update(entity) → 返回 true/false
  │  4. IUnitOfWork.SaveChangesAsync()
  │
  ▼
MistakeServiceImpl:
  │  5. success == false → RpcException(Internal, "更新失败")
  │  6. GetMistakeItemAsync(id) 重新查询
  │     └─ null → RpcException(NotFound)
  │  7. MapMistakeItemDto(updatedItem)
  │
  ▼
Client ← MistakeItemDto
```

## 关键设计决策

1. **部分更新在 gRPC 层实现**：字段选择逻辑放在 `MistakeServiceImpl`，领域服务仅负责持久化，保持领域服务职责单一。
2. **更新后重新查询**：为确保返回数据与数据库一致，更新成功后重新查询而非直接返回内存中的 model。
3. **UpdatedAt 的 ConcurrencyCheck**：`MistakeItemEntity.UpdatedAt` 标注了 `[ConcurrencyCheck]`，用于乐观并发控制。
4. **SourceRegions 整体替换**：当请求中包含 source_regions 时，整体替换而非合并，简化实现。
