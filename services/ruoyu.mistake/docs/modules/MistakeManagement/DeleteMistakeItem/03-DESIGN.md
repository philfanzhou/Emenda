# DeleteMistakeItem - 设计文档

## 文件结构

| 文件 | 项目相对路径 |
|------|-------------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` |
| 仓储接口 | `src/Domain/Repositories/IMistakeItemRepository.cs`, `IMistakePracticeRepository.cs`, `IProcessLogRepository.cs` |
| 仓储实现 | `src/Database/Repositories/MistakeItemRepository.cs`, `MistakePracticeRepository.cs`, `ProcessLogRepository.cs` |
| 数据实体 | `src/Database/Entities/MistakeItemEntity.cs`, `MistakePracticeEntity.cs`, `ProcessLogEntity.cs` |
| 单元测试 | `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs` |

## 关键接口签名

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<BoolResponse> DeleteMistakeItem(IdRequest request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<bool> DeleteMistakeItemAsync(Guid id)
```

### 仓储接口

```csharp
// IMistakeItemRepository.cs
Task<MistakeItemModel?> GetByIdAsync(Guid id);
Task<bool> DeleteAsync(Guid id);

// IMistakePracticeRepository.cs
Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);

// IProcessLogRepository.cs
Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);
```

## 依赖的数据库表

| 表名 | 说明 |
|------|------|
| `mistake_practice` | 练习记录表，按 mistake_item_id 删除 |
| `process_log` | 操作日志表，按 mistake_item_id 删除 |
| `mistake_item` | 错题条目主表，最后删除 |

> 数据库 Schema 详情参见 `src/Database/MistakeDbContext.cs`。mistake_practice 和 process_log 均配置了 `OnDelete(DeleteBehavior.Cascade)`。

## 数据流 / 调用链

```
Client
  │
  ▼
MistakeServiceImpl.DeleteMistakeItem(request)
  │
  ▼
MistakeDomainService.DeleteMistakeItemAsync(Guid.Parse(request.Id))
  │  1. IMistakeItemRepository.GetByIdAsync(id)
  │     └─ null → return false
  │
  │  2. IMistakePracticeRepository.RemoveByMistakeItemIdAsync(id)
  │     └─ 查询所有匹配记录 → RemoveRange
  │
  │  3. IProcessLogRepository.RemoveByMistakeItemIdAsync(id)
  │     └─ 查询所有匹配记录 → RemoveRange
  │
  │  4. IMistakeItemRepository.DeleteAsync(id)
  │     └─ FindAsync → Remove
  │
  │  5. IUnitOfWork.SaveChangesAsync()
  │     └─ 统一提交事务
  │
  ▼
MistakeServiceImpl: BoolResponse { Success = result }
  │
  ▼
Client ← BoolResponse
```

## 关键设计决策

1. **显式删除子表**：即使数据库配置了 `CASCADE` 删除，代码层仍显式调用 `RemoveByMistakeItemIdAsync`，保证逻辑清晰且便于日志追踪。
2. **删除顺序**：practices → process_logs → mistake_item，先删子表再删主表，避免外键约束问题。
3. **不存在时返回 false 而非异常**：删除不存在的条目视为幂等操作，返回 `success=false` 而非抛出异常。
4. **统一事务**：所有删除操作在同一 `SaveChangesAsync` 中提交，保证原子性。
