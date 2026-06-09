# DeleteMistakeItem — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 DeleteMistakeItemAsync 方法：确认查询后返回 false 的逻辑、三级删除顺序（练习记录 → 操作日志 → 错题条目）、事务提交使用 SaveChangesAsync。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；删除顺序与文档描述一致。",
    "notes": "确认不检查 ReviewStatus——任意状态的条目均可删除。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IMistakePracticeRepository.cs 与 IProcessLogRepository.cs 中 RemoveByMistakeItemIdAsync 方法：确认方法签名存在且参数为 Guid mistakeItemId。",
    "files": ["src/Domain/Repositories/IMistakePracticeRepository.cs", "src/Domain/Repositories/IProcessLogRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "RemoveByMistakeItemIdAsync 返回 Task（非 Task<bool>），表示批量删除。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 IMistakeItemRepository.cs 中 DeleteAsync 方法：确认返回 Task<bool>，参数为 Guid id。",
    "files": ["src/Domain/Repositories/IMistakeItemRepository.cs"],
    "acceptance": "dotnet build；DeleteAsync 存在于接口和实现中。",
    "notes": "任何缺失方法都必须在实现中补齐或在评审结论中明确标注风险。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 DeleteMistakeItem 方法：确认 try-catch 逻辑、MistakeValidationException 转换为 InvalidArgument、正常返回 BoolResponse。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；异常处理覆盖 MistakeValidationException。",
    "notes": "Guid.Parse 失败不在 try-catch 范围内，会由 gRPC 框架处理。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04"],
    "action": "执行删除成功测试（Confirmed 状态）：创建 Confirmed 状态的条目，验证删除返回 true，且三个 RemoveByXxx/DeleteAsync 各被调用一次，SaveChangesAsync 被调用一次。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~DeleteMistakeItemAsync_WhenConfirmed_ShouldDelete",
    "notes": "现有测试已覆盖。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行删除成功测试（PendingReview 状态）：创建 PendingReview 状态的条目，验证删除成功，行为与 Confirmed 状态一致。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~DeleteMistakeItemAsync_WhenPendingReview_ShouldDelete",
    "notes": "现有测试已覆盖。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行条目不存在测试：mock GetByIdAsync 返回 null，验证返回 false。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~DeleteMistakeItemAsync_WhenNotExists",
    "notes": "确认不调用 RemoveByXxx/DeleteAsync/SaveChangesAsync。"
  },
  {
    "id": "TEST-04",
    "depends_on": ["TEST-01"],
    "action": "执行事务失败测试：mock SaveChangesAsync 抛出异常，验证异常向上传播。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~DeleteMistakeItemAsync_TransactionFailure",
    "notes": "事务回滚是数据一致性的关键保障。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03", "TEST-04"],
    "action": "在 Release 配置下编译解决方案并运行所有测试，打印覆盖率摘要。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~MistakeDomainService",
    "notes": "必须零警告，无测试失败。"
  }
]
```