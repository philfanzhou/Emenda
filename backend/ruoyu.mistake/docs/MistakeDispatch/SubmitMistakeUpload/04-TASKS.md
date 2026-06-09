# SubmitMistakeUpload — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 MistakeItemModel.cs 与 SourceRegionModel.cs 字段与数据模型：确认 StudentId/Subject/Grade/SourceUploadId/SourceRegions/ReviewStatus/CreatedAt 的命名、默认值与文档语义与 SPEC 一致；确认 ReviewStatus 默认为 int 类型。",
    "files": ["src/Domain/Models/MistakeItemModel.cs"],
    "acceptance": "代码阅读签名与注释一致；编译通过 dotnet build。",
    "notes": "BoundingBox 字段允许 null，SubmitMistakeUpload 场景下始终为 null。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IMistakeItemRepository.cs 仓储接口：确认 AddAsync 方法签名正确，确认 GetBySourceUploadIdAsync 方法存在。",
    "files": ["src/Domain/Repositories/IMistakeItemRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "任何缺失方法都必须在实现中补齐或在评审结论中明确标注风险。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeItemEntity.cs 实体：确认 StudentId 的 MaxLength(36) 约束、source_upload_id 的 MaxLength(64) 约束、updated_at 的 ConcurrencyCheck 标注、表名 mistake_item。",
    "files": ["src/Database/Entities/MistakeItemEntity.cs"],
    "acceptance": "编译通过；实体字段与数据库列名、约束一致。",
    "notes": "source_regions 以 JSONB 格式存储，需确认 SourceRegionEntry 与 EF Core ValueConverter 配置正确。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 SubmitMistakeUpload 方法：确认参数校验逻辑（student_id 非空、subject 1-9、grade 1-12、source_upload_id 非空、image_paths 非空）与 SPEC 完全一致。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；所有 InvalidArgument 场景均有覆盖。",
    "notes": "gRPC 层和领域层均已做校验，确认为有意设计的双层校验而非冗余。"
  },
  {
    "id": "REVIEW-05",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 SubmitMistakeUploadAsync 方法与 ValidateSubmitData 静态方法：确认校验逻辑聚合所有错误、SourceRegions 转换正确、ReviewStatus 设置为 PendingReview、操作日志 Action 为 Created。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；ValidateSubmitData 收集所有错误并以 '; ' 分隔。",
    "notes": "ValidateSubmitData 为 static 方法，具有独立可测试性。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04", "REVIEW-05"],
    "action": "执行提交主流程测试：使用有效参数创建错题条目，断言返回的 items 数量为 1、SourceRegions 数量与 imagePaths 一致、ReviewStatus 为 PendingReview、BoundingBox 均为 null。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~SubmitMistakeUploadAsync_ShouldCreateItemWithSourceRegions",
    "notes": "事务提交后仓储应收到 AddAsync 调用，UoW.SaveChangesAsync 应被调用一次。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行参数校验测试：通过领域服务传入空 student_id、无效 subject、无效 grade、空 image_paths、空 source_upload_id，断言均抛出 MistakeValidationException。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~SubmitMistakeUpload",
    "notes": "校验错误消息应包含所有失败项（以 '; ' 分隔）。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行事务一致性测试：模拟 SaveChangesAsync 抛出异常，断言错题条目和操作日志均未写入数据库。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~SubmitMistakeUpload",
    "notes": "事务回滚是数据一致性的关键保障。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03"],
    "action": "在 Release 配置下编译解决方案并运行所有相关测试。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~SubmitMistakeUpload",
    "notes": "必须零警告，无测试失败。"
  }
]
```