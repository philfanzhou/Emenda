```json
[
  {
    "id": "T-01",
    "status": "implemented",
    "depends_on": [],
    "action": "实现 SubmitMistakeUpload RPC 接口及 Proto 定义",
    "files": [
      "src/Contract/Protos/mistake.proto",
      "src/Contract/Protos/mistake.common.proto"
    ],
    "acceptance": "Proto 定义 SubmitMistakeUploadRequest/Response，gRPC service 包含 SubmitMistakeUpload RPC"
  },
  {
    "id": "T-02",
    "status": "implemented",
    "depends_on": ["T-01"],
    "action": "实现领域服务 SubmitMistakeUploadAsync 方法",
    "files": [
      "src/Domain/Services/MistakeDomainService.cs",
      "src/Domain/Models/MistakeItemModel.cs",
      "src/Domain/Models/ProcessLogModel.cs"
    ],
    "acceptance": "创建 MistakeItemModel（ReviewStatus=PendingReview），每个 imagePath 创建 SourceRegionModel（BoundingBox=null），记录 ProcessLog（Action=Created）"
  },
  {
    "id": "T-03",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "实现 gRPC 层 MistakeServiceImpl.SubmitMistakeUpload",
    "files": [
      "src/Service/MistakeServiceImpl.cs"
    ],
    "acceptance": "参数验证（studentId/subject/grade/sourceUploadId/imagePaths），MistakeValidationException → RpcException(InvalidArgument)，返回 created_item_ids"
  },
  {
    "id": "T-04",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "实现领域层参数验证 ValidateSubmitData",
    "files": [
      "src/Domain/Services/MistakeDomainService.cs"
    ],
    "acceptance": "验证 studentId 非空、subject 1-9、grade 1-12、sourceUploadId 非空、imagePaths 非空，多错误合并返回"
  },
  {
    "id": "T-05",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "实现仓储层 MistakeItemRepository.AddAsync",
    "files": [
      "src/Database/Repositories/MistakeItemRepository.cs",
      "src/Database/Entities/MistakeItemEntity.cs"
    ],
    "acceptance": "MistakeItemModel 正确映射为 MistakeItemEntity 并持久化"
  },
  {
    "id": "T-06",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "编写单元测试",
    "files": [
      "test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"
    ],
    "acceptance": "覆盖正常提交流程和重复提交场景"
  }
]
```
