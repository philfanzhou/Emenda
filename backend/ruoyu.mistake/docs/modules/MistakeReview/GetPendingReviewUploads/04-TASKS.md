```json
[
  {
    "id": "T-01",
    "status": "implemented",
    "depends_on": [],
    "action": "实现 GetPendingReviewUploads RPC 接口及 Proto 定义",
    "files": [
      "src/Contract/Protos/mistake.proto",
      "src/Contract/Protos/mistake.common.proto"
    ],
    "acceptance": "Proto 定义 GetPendingReviewUploadsRequest/PendingReviewUploadListResponse/PendingReviewUploadDto/PageMeta"
  },
  {
    "id": "T-02",
    "status": "implemented",
    "depends_on": ["T-01"],
    "action": "实现领域服务 GetPendingReviewUploadsAsync 方法",
    "files": [
      "src/Domain/Services/MistakeDomainService.cs"
    ],
    "acceptance": "分页参数修正，调用仓储层获取分组结果"
  },
  {
    "id": "T-03",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "实现仓储层客户端分组聚合逻辑",
    "files": [
      "src/Database/Repositories/MistakeItemRepository.cs"
    ],
    "acceptance": "查询 PendingReview 条目，客户端按 SourceUploadId/StudentId/Subject/Grade 分组，计算 PendingCount 和 EarliestCreatedAt，客户端分页"
  },
  {
    "id": "T-04",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "实现 gRPC 层 MistakeServiceImpl.GetPendingReviewUploads",
    "files": [
      "src/Service/MistakeServiceImpl.cs"
    ],
    "acceptance": "解析 subjects，构建 PageMeta，映射 PendingReviewUploadModel→PendingReviewUploadDto，EarliestCreatedAt 格式化"
  },
  {
    "id": "T-05",
    "status": "implemented",
    "depends_on": ["T-03"],
    "action": "实现 subjects 筛选",
    "files": [
      "src/Database/Repositories/MistakeItemRepository.cs"
    ],
    "acceptance": "subjects 非空时按 Subject 筛选，为空时不筛选"
  },
  {
    "id": "T-06",
    "status": "implemented",
    "depends_on": ["T-02"],
    "action": "编写单元测试",
    "files": [
      "test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"
    ],
    "acceptance": "覆盖基本查询场景，验证分页参数修正和结果返回"
  }
]
```
