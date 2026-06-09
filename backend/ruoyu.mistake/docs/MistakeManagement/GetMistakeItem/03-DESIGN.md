# GetMistakeItem — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.mistake/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── MistakeItemModel.cs              # 错题条目领域模型
│   ├── Services/
│   │   └── MistakeDomainService.cs              # 领域服务 (GetMistakeItemAsync, GetMistakeItemsByUploadAsync, GetMistakeItemListAsync)
│   ├── Repositories/
│   │   └── IMistakeItemRepository.cs            # 错题条目仓储接口 (GetByIdAsync, GetBySourceUploadIdAsync, GetListAsync)
│   ├── Database/
│   │   └── Repositories/
│   │       └── MistakeItemRepository.cs         # 错题条目仓储实现
│   ├── Service/
│   │   └── MistakeServiceImpl.cs                # gRPC 实现 (GetMistakeItem, GetMistakeItemsByUpload, GetMistakeItemList)
│   └── Contract/Protos/
│       ├── mistake.proto                        # gRPC 契约定义
│       └── mistake.common.proto                 # 公共消息定义 (MistakeItemDto, PageMeta, IdRequest)
├── test/
│   └── Ruoyu.Study.Mistake.Domain.Tests/
│       └── MistakeDomainServiceTests.cs         # 单元测试
└── docs/MistakeManagement/GetMistakeItem/       # 本文档所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 关键接口签名和数据结构定义

### 领域服务方法签名

```csharp
// src/Domain/Services/MistakeDomainService.cs

// 按 ID 查询单条
public async Task<MistakeItemModel?> GetMistakeItemAsync(Guid id)

// 按上传记录 ID 查询列表
public async Task<List<MistakeItemModel>> GetMistakeItemsByUploadAsync(string sourceUploadId)

// 分页条件查询
public async Task<(List<MistakeItemModel> Items, int TotalCount)> GetMistakeItemListAsync(
    string studentId, int subject, int grade, int reviewStatus, int page, int size)
```

### 仓储接口（查询相关方法）

```csharp
// src/Domain/Repositories/IMistakeItemRepository.cs
Task<MistakeItemModel?> GetByIdAsync(Guid id);
Task<List<MistakeItemModel>> GetBySourceUploadIdAsync(string sourceUploadId);
Task<(List<MistakeItemModel> Items, int TotalCount)> GetListAsync(string studentId, int subject, int grade, int reviewStatus, int page, int size);
```

### gRPC 接口

```protobuf
// src/Contract/Protos/mistake.proto

// 单条查询
rpc GetMistakeItem(IdRequest) returns (MistakeItemDto);

// 按上传记录查询
rpc GetMistakeItemsByUpload(GetMistakeItemsByUploadRequest) returns (MistakeItemListResponse);

// 分页条件查询
rpc GetMistakeItemList(GetMistakeItemListRequest) returns (MistakeItemPageResult);

message IdRequest {
  string id = 1;
}

message GetMistakeItemsByUploadRequest {
  string source_upload_id = 1;
}

message MistakeItemListResponse {
  repeated MistakeItemDto items = 1;
}

message GetMistakeItemListRequest {
  string student_id = 1;
  int32 subject = 2;
  int32 grade = 3;
  ReviewStatus review_status = 4;
  int32 page = 5;
  int32 size = 6;
}

message MistakeItemPageResult {
  repeated MistakeItemDto items = 1;
  PageMeta page_meta = 2;
}

message PageMeta {
  int32 page = 1;
  int32 size = 2;
  int32 total_count = 3;
  int32 total_pages = 4;
}
```

### gRPC 层 MapMistakeItemDto 方法

```csharp
// src/Service/MistakeServiceImpl.cs
private static MistakeItemDto MapMistakeItemDto(MistakeItemModel item)
{
    var dto = new MistakeItemDto
    {
        Id = item.Id.ToString(),
        StudentId = item.StudentId ?? string.Empty,
        Subject = item.Subject,
        Grade = item.Grade,
        SourceUploadId = item.SourceUploadId ?? string.Empty,
        ReviewStatus = (ReviewStatus)item.ReviewStatus,
        ReviewerId = item.ReviewerId ?? string.Empty,
        ReviewedAt = item.ReviewedAt.HasValue ? ... : string.Empty,
        ReviewComment = item.ReviewComment ?? string.Empty,
        Type = item.Type,
        QuestionId = item.QuestionId ?? string.Empty,
        CreatedAt = item.CreatedAt == DateTimeOffset.MinValue ? string.Empty : ...,
        UpdatedAt = item.UpdatedAt.HasValue ? ... : string.Empty
    };

    foreach (var region in item.SourceRegions ?? new())
    {
        var regionDto = new SourceRegion { SourceImagePath = region.SourceImagePath ?? string.Empty };
        if (region.BoundingBox != null)
            regionDto.BoundingBox = new BoundingBox { X1 = ..., Y1 = ..., X2 = ..., Y2 = ... };
        dto.SourceRegions.Add(regionDto);
    }

    return dto;
}
```

## 数据流描述（步骤序列）

### GetMistakeItem — 单条查询

1. gRPC 层接收 `IdRequest`，解析 ID 为 `Guid`。
2. 调用 `MistakeDomainService.GetMistakeItemAsync(id)`。
3. 领域服务调用 `_mistakeItemRepository.GetByIdAsync(id)` 返回 `MistakeItemModel?`。
4. gRPC 层判断结果：null → 抛出 `RpcException(StatusCode.NotFound, "错题条目不存在")`；非 null → 调用 `MapMistakeItemDto` 映射后返回。

### GetMistakeItemsByUpload — 按上传记录查询

1. gRPC 层接收 `GetMistakeItemsByUploadRequest`，取 `SourceUploadId`。
2. 调用 `MistakeDomainService.GetMistakeItemsByUploadAsync(sourceUploadId)`。
3. 领域服务调用 `_mistakeItemRepository.GetBySourceUploadIdAsync(sourceUploadId)` 返回 `List<MistakeItemModel>`。
4. gRPC 层将所有条目通过 `MapMistakeItemDto` 映射，填充 `MistakeItemListResponse.Items` 返回。

### GetMistakeItemList — 分页条件查询

1. gRPC 层接收 `GetMistakeItemListRequest`，取各过滤字段、page、size。
2. 调用 `MistakeDomainService.GetMistakeItemListAsync(studentId, subject, grade, reviewStatus, page, size)`。
3. 领域服务内部进行 page/size 规范化：
   - `page <= 0` → page = 1
   - `size <= 0` → size = 10
   - `size > 100` → size = 100
4. 调用 `_mistakeItemRepository.GetListAsync(...)` 返回 `(Items, TotalCount)`。
5. gRPC 层构造 `PageMeta`：`page = request.Page`, `size = request.Size`, `total_count`, `total_pages = (total_count + size - 1) / size`。
6. 将所有条目通过 `MapMistakeItemDto` 映射，填充 `MistakeItemPageResult.Items` 和 `PageMeta` 返回。

## 错误处理策略

- **条目不存在**：`GetMistakeItem` 在 gRPC 层检查 null 后抛出 `RpcException(StatusCode.NotFound)`。
- **空列表**：`GetMistakeItemsByUpload` 和 `GetMistakeItemList` 返回空 items 列表，不抛异常。
- **参数规范化**：page/size 的非法值（≤0、>100）在领域服务层自动修正，不抛异常。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `IMistakeItemRepository` | 错题条目 GetByIdAsync / GetBySourceUploadIdAsync / GetListAsync | `Ruoyu.Study.Mistake.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`IMistakeItemRepository` 通过构造函数注入，可被 Moq 替换。
- **查询为纯读操作**：不涉及 `SaveChangesAsync`，测试仅需验证仓储方法的调用和返回值映射。
- **分页逻辑可独立测试**：page/size 规范化逻辑在领域服务中实现，可直接测试边界值（page=0、size=0、size=200）。
- **DTO 映射可独立测试**：`MapMistakeItemDto` 为静态方法，可单独测试各种边界情况（null 字段、空 SourceRegions 等）。