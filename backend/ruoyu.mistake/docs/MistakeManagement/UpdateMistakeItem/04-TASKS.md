# UpdateMistakeItem — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 UpdateMistakeItem 方法：确认先查后改逻辑（GetMistakeItemAsync → 部分更新 → UpdateMistakeItemAsync → 重新 GetMistakeItemAsync）；确认部分更新的判断条件（!IsNullOrWhiteSpace、> 0、Count > 0）与 SPEC 一致。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；部分更新逻辑覆盖所有四个字段。",
    "notes": "必须确认 NotFound 和 Internal 错误码分别用于条目不存在和更新失败场景。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 UpdateMistakeItemAsync 方法：确认调用 _mistakeItemRepository.UpdateAsync + _unitOfWork.SaveChangesAsync，返回 bool。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；UpdateAsync 调用后 SaveChangesAsync 被调用。",
    "notes": "UpdateMistakeItemAsync 不记录 process_log，与 AddMistakeItemAsync 区分。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 IMistakeItemRepository.cs 中 UpdateAsync 方法签名：确认返回 Task<bool>，参数为 MistakeItemModel。",
    "files": ["src/Domain/Repositories/IMistakeItemRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "任何缺失方法都必须在实现中补齐或在评审结论中明确标注风险。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 UpdateMistakeItemRequest proto 定义：确认字段 id/student_id/subject/grade/source_regions 与实现一致。",
    "files": ["src/Contract/Protos/mistake.proto"],
    "acceptance": "编译通过；proto 字段编号与实现一致。",
    "notes": "UpdateMistakeItemRequest 不包含 comments 字段，与 AddMistakeItemRequest 不同。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04"],
    "action": "执行领域服务更新测试：创建 MistakeItemModel，mock UpdateAsync 返回 true，断言返回值 true 且 SaveChangesAsync 被调用一次。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~UpdateMistakeItemAsync_ShouldUpdateItem",
    "notes": "现有测试已覆盖此场景。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行 gRPC 层更新测试：传入有效 ID 和部分字段，mock GetByIdAsync 返回已有条目，验证更新后返回的 DTO 字段正确；传入不存在的 ID，验证抛出 NotFound。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~UpdateMistakeItem",
    "notes": "验证部分更新的四个字段各自的独立更新行为。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行更新失败测试：mock UpdateAsync 返回 false，验证抛出 RpcException(StatusCode.Internal)。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~UpdateMistakeItemAsync_ShouldReturnFalse",
    "notes": "确认 Internal 错误消息为 '更新失败'。"
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