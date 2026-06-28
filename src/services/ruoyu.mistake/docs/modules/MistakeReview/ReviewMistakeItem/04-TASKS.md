```json
[
  {
    "id": "T-01",
    "status": "implemented",
    "depends_on": [],
    "action": "实现 ReviewMistakeItem RPC 接口及 Proto 定义",
    "files": [
      "src/Contract/Protos/mistake.proto",
      "src/Contract/Protos/mistake.common.proto"
    ],
    "acceptance": "Proto 定义 ReviewMistakeItemRequest/Response，gRPC service 包含 ReviewMistakeItem RPC"
  },
  {
    "id": "T-02",
    "status": "implemented",
    "depends_on": ["T-01"],
    "action": "实现领域服务 ReviewMistakeItemAsync 方法",
    "files": [
      "src/Domain/Services/MistakeDomainService.cs"
    ],
    "acceptance": "验证 PendingReview 状态、审核结果为 Confirmed/Rejected，更新审核字段，可选更新 grade/subject，记录 ProcessLog"
  },
  {
    "id": "T-03",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "实现 gRPC 层 MistakeServiceImpl.ReviewMistakeItem",
    "files": [
      "src/Service/MistakeServiceImpl.cs"
    ],
    "acceptance": "验证审核状态为 Confirmed/Rejected，Confirmed 时迁移图片 uploads/→mistakes/，收集 removed_image_paths，MistakeValidationException → RpcException(InvalidArgument)"
  },
  {
    "id": "T-04",
    "status": "implemented",
    "depends_on": ["T-03"],
    "action": "实现 Confirmed 时的图片迁移逻辑",
    "files": [
      "src/Service/MistakeServiceImpl.cs"
    ],
    "acceptance": "遍历 SourceRegions，uploads/ 前缀的图片执行 OSS Copy+Delete，更新 SourceImagePath，调用 UpdateMistakeItemAsync 持久化"
  },
  {
    "id": "T-05",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "实现可选 grade/subject 更新",
    "files": [
      "src/Domain/Services/MistakeDomainService.cs",
      "src/Service/MistakeServiceImpl.cs"
    ],
    "acceptance": "gRPC 层 grade>0 和 subject>0 才传入，领域层 grade 1-12 和 subject 1-9 才更新"
  },
  {
    "id": "T-06",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "编写单元测试",
    "files": [
      "test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"
    ],
    "acceptance": "覆盖确认审核、退回审核、重复审核场景"
  }
]
```
