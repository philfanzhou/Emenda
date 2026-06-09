# GetMistakeItem — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中三个查询方法：确认 GetMistakeItemAsync 返回 MistakeItemModel?、GetMistakeItemsByUploadAsync 返回 List<MistakeItemModel>、GetMistakeItemListAsync 返回 (List<MistakeItemModel>, int) 元组且含 page/size 规范化逻辑。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；page/size 规范化：page<=0→1, size<=0→10, size>100→100。",
    "notes": "GetMistakeItemListAsync 中 reviewStatus 参数为 int 类型（非 enum），使用 proto 定义的整数值。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IMistakeItemRepository.cs 中查询方法签名：确认 GetByIdAsync 返回 MistakeItemModel?、GetBySourceUploadIdAsync 返回 List<MistakeItemModel>、GetListAsync 返回元组。",
    "files": ["src/Domain/Repositories/IMistakeItemRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "任何缺失方法都必须在实现中补齐或在评审结论中明确标注风险。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中三个查询的 gRPC 方法：确认 GetMistakeItem null 检查抛 NotFound、GetMistakeItemsByUpload 直接映射、GetMistakeItemList 构造 PageMeta（total_pages 计算正确）。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；total_pages 公式为 (totalCount + request.Size - 1) / request.Size。",
    "notes": "MapMistakeItemDto 中 CreatedAt 为 DateTimeOffset.MinValue 时返回空字符串，UpdatedAt/ReviewedAt 为 null 时返回空字符串。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 proto 定义：确认 GetMistakeItemListRequest 字段（student_id/subject/grade/review_status/page/size）、MistakeItemPageResult 含 page_meta、MistakeItemListResponse 含 items。",
    "files": ["src/Contract/Protos/mistake.proto", "src/Contract/Protos/mistake.common.proto"],
    "acceptance": "编译通过；proto 字段编号与实现一致。",
    "notes": "PageMeta 定义在 mistake.common.proto 中，被 MistakeItemPageResult 复用。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04"],
    "action": "执行 GetMistakeItem 单元测试：mock GetByIdAsync 返回有效条目，验证返回非 null；mock 返回 null，验证领域服务返回 null（gRPC 层应抛 NotFound）。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetMistakeItem",
    "notes": "验证 MapMistakeItemDto 映射正确性。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行 GetMistakeItemsByUpload 单元测试：mock GetBySourceUploadIdAsync 返回列表，验证返回相同数量条目。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetMistakeItemsByUpload",
    "notes": "空列表场景也应覆盖。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行 GetMistakeItemList 分页测试：验证 page/size 规范化（page=0→1, size=0→10, size=200→100）；验证 PageMeta 计算正确。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetMistakeItemList",
    "notes": "total_pages 公式为整数除法向上取整。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03"],
    "action": "在 Release 配置下编译解决方案并运行所有测试，打印覆盖率摘要。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~MistakeDomainService",
    "notes": "必须零警告，无测试失败。"
  }
]
```