# AddMistakePractice - 设计文档

## 文件结构

| 文件 | 项目相对路径 |
|------|-------------|
| Proto 定义 | `src/Contract/Protos/mistake.proto` |
| 公共 Proto | `src/Contract/Protos/mistake.common.proto` |
| gRPC 实现 | `src/Service/MistakeServiceImpl.cs` |
| 领域服务 | `src/Domain/Services/MistakeDomainService.cs` |
| 领域模型 | `src/Domain/Models/MistakePracticeModel.cs` |
| 仓储接口 | `src/Domain/Repositories/IMistakePracticeRepository.cs`, `IMistakeItemRepository.cs` |
| 仓储实现 | `src/Database/Repositories/MistakePracticeRepository.cs`, `MistakeItemRepository.cs` |
| 数据实体 | `src/Database/Entities/MistakePracticeEntity.cs` |
| 单元测试 | `test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs` |

## 关键接口签名

### gRPC 层

```csharp
// MistakeServiceImpl.cs
public override async Task<MistakePracticeDto> AddMistakePractice(AddMistakePracticeRequest request, ServerCallContext context)
```

### 领域服务

```csharp
// MistakeDomainService.cs
public async Task<MistakePracticeModel> AddPracticeAsync(MistakePracticeModel practice, List<string>? imagePaths = null)
```

### 仓储接口

```csharp
// IMistakeItemRepository.cs
Task<MistakeItemModel?> GetByIdAsync(Guid id);

// IMistakePracticeRepository.cs
Task AddAsync(MistakePracticeModel model);
```

## 依赖的数据库表

| 表名 | 说明 |
|------|------|
| `mistake_item` | 错题条目主表，用于验证存在性和审核状态 |
| `mistake_practice` | 练习记录表，存储练习数据 |
| `process_log` | 操作日志表，记录 Practiced 事件 |

> 数据库 Schema 详情参见 `src/Database/MistakeDbContext.cs`。mistake_practice 通过 `MistakeItemId` 外键关联 mistake_item，配置 `OnDelete(DeleteBehavior.Cascade)`。

## 数据流 / 调用链

```
Client
  │
  ▼
MistakeServiceImpl.AddMistakePractice(request)
  │  1. 构造 MistakePracticeModel（映射字段）
  │  2. 提取 imagePaths（request.ImagePaths.Count > 0 时）
  │
  ▼
MistakeDomainService.AddPracticeAsync(practice, imagePaths)
  │  3. IMistakeItemRepository.GetByIdAsync(practice.MistakeItemId)
  │     ├─ null → MistakeValidationException("错题条目不存在")
  │     └─ ReviewStatus != Confirmed → MistakeValidationException("只能对已确认的错题进行练习")
  │
  │  4. practice.Id = Guid.NewGuid()
  │  5. practice.PracticeDate = DateTimeOffset.UtcNow
  │  6. imagePaths 非空且非空列表 → practice.ImagePaths = imagePaths
  │
  ▼
IMistakePracticeRepository.AddAsync(practice)
  │  7. Model → Entity 映射
  │  8. DbContext.MistakePractices.AddAsync(entity)
  │
  ▼
AddProcessLogAsync(item.Id, Practiced, "Student practiced mistake")
  │  9. 创建 ProcessLogModel
  │  10. IProcessLogRepository.AddAsync(log)
  │
  ▼
IUnitOfWork.SaveChangesAsync()
  │  11. 提交事务
  │
  ▼
MistakeServiceImpl: MapPracticeDto(result) → MistakePracticeDto
  │
  ▼
Client ← MistakePracticeDto
```

## 关键设计决策

1. **先验证后创建**：必须先验证错题存在且已确认，才允许创建练习记录，保证数据一致性。
2. **imagePaths 作为可选参数**：通过方法参数而非 model 属性传递，区分"未提供"和"空列表"两种语义。
3. **ProcessLog 记录在错题维度**：日志关联到 mistake_item 而非 practice，便于追踪错题的完整生命周期。
4. **PracticeDate 使用 UtcNow**：统一使用 UTC 时间，避免时区问题。
