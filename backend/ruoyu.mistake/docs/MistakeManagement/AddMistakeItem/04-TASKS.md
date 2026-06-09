# AddMistakeItem — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 MistakeItemModel.cs、SourceRegionModel.cs 与 BoundingBoxModel.cs 模型字段：确认 Id/StudentId/Subject/Grade/SourceUploadId/SourceRegions/ReviewStatus/CreatedAt 的命名、默认值与文档语义与 SPEC 一致；确认 SourceRegionModel.BoundingBox 为可空类型。",
    "files": ["src/Domain/Models/MistakeItemModel.cs"],
    "acceptance": "代码阅读签名与注释一致；编译通过 dotnet build。",
    "notes": "模型字段必须与 MistakeItemRepository 中的映射保持严格一致。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IMistakeItemRepository.cs 与 IProcessLogRepository.cs 仓储接口：确认 AddAsync 方法存在且签名正确。",
    "files": ["src/Domain/Repositories/IMistakeItemRepository.cs", "src/Domain/Repositories/IProcessLogRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "任何缺失方法都必须在实现中补齐或在评审结论中明确标注风险。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 AddMistakeItemAsync 方法：确认校验逻辑（student_id 非空、subject 0-9、grade 0-12、source_upload_id 非空、source_regions 非空）与 SPEC 一致；确认 ReviewStatus 设为 PendingReview、操作日志记录正确。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；所有 InvalidArgument 场景均有覆盖。",
    "notes": "ValidateMistakeItemData 错误消息格式为分号分隔的中文消息。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 AddMistakeItem 方法：确认 request 映射到 MistakeItemModel 的逻辑（包括 SourceRegion 和 BoundingBox 的可选映射）；确认异常 catch 后转换为 RpcException(StatusCode.InvalidArgument)。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；映射逻辑与 proto 定义一致。",
    "notes": "BoundingBox 为 null 时 SourceRegionModel.BoundingBox 应设为 null。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04"],
    "action": "执行创建主流程测试：使用有效参数创建错题条目（含两个 SourceRegion，一个有 BoundingBox 一个没有），断言返回的 Model.Id != Guid.Empty、ReviewStatus==PendingReview、SourceRegions 数量正确、操作日志已记录。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AddMistakeItemAsync_WithSourceRegions_ShouldSucceed",
    "notes": "验证 Movk<IProcessLogRepository>.AddAsync 被调用，Mock<IUnitOfWork>.SaveChangesAsync 被调用。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行参数校验测试：通过领域服务传入空 StudentId、Subject=-1、Subject=10、Grade=-1、Grade=13、空 SourceUploadId、空 SourceRegions，断言均抛出 MistakeValidationException。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AddMistakeItemAsync_Validation",
    "notes": "每种无效参数独立测试，确保错误消息描述对应字段。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行事务一致性测试：模拟 SaveChangesAsync 抛出异常，断言错题条目和操作日志均未写入数据库。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AddMistakeItemAsync_TransactionFailure",
    "notes": "事务回滚是数据一致性的关键保障。"
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