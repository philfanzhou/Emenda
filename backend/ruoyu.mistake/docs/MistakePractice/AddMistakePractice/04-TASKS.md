# AddMistakePractice — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 MistakePracticeModel.cs 字段与数据模型：确认 Id/MistakeItemId/StudentId/PracticeDate/ImagePaths/StudentAnswer/IsCorrect/Comments 的命名、默认值与文档语义一致。",
    "files": ["src/Domain/Models/MistakePracticeModel.cs"],
    "acceptance": "代码阅读签名与注释一致；编译通过 dotnet build。",
    "notes": "ImagePaths 默认为空列表，StudentAnswer 和 Comments 允许 null。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IMistakePracticeRepository.cs 仓储接口：确认 AddAsync 方法存在且签名正确，确认 GetByMistakeItemIdAsync 方法存在。",
    "files": ["src/Domain/Repositories/IMistakePracticeRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "任何缺失方法都必须在实现中补齐或在评审结论中明确标注风险。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakePracticeEntity.cs 实体：确认 mistake_item_id 的 FK 关系、student_id 的 MaxLength(36) 约束、表名 mistake_practice。",
    "files": ["src/Database/Entities/MistakePracticeEntity.cs"],
    "acceptance": "编译通过；实体字段与数据库列名、约束一致。",
    "notes": "image_paths 以 JSONB 格式存储，需确认 EF Core ValueConverter 配置正确。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 AddMistakePractice 方法：确认从 request 构建 MistakePracticeModel 时的字段映射正确（MistakeItemId 解析为 Guid、ImagePaths 可选）。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；request → model 映射无遗漏字段。",
    "notes": "image_paths 为 null 或空列表时不传入领域服务，由领域服务内部处理默认值。"
  },
  {
    "id": "REVIEW-05",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 AddPracticeAsync 方法：确认错题条目存在性校验、状态校验（仅 Confirmed 允许练习）、PracticeDate 赋值、操作日志 Action 为 Practiced。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；两条错误路径（不存在、非 Confirmed）均有 MistakeValidationException 抛出。",
    "notes": "操作日志的 Details 固定为 'Student practiced mistake'。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04", "REVIEW-05"],
    "action": "执行添加练习成功测试：使用 Confirmed 状态的错题条目和有效的练习参数，断言返回的 Practice.Id != Guid.Empty、PracticeDate 接近当前时间、IsCorrect 与入参一致。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AddPracticeAsync_WhenItemConfirmed_ShouldAddPractice",
    "notes": "事务提交后 UoW.SaveChangesAsync 应被调用一次。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行错题条目不存在测试：mock GetByIdAsync 返回 null，断言抛出 MistakeValidationException 且消息包含 '错题条目不存在'。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AddPracticeAsync",
    "notes": ""
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行错题条目非 Confirmed 状态测试：mock 返回 PendingReview 状态的错题条目，断言抛出 MistakeValidationException 且消息包含 '只能对已确认的错题进行练习'。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AddPracticeAsync_WhenItemNotConfirmed_ShouldThrow",
    "notes": "此测试验证 SPEC FR-03 的核心约束。"
  },
  {
    "id": "TEST-04",
    "depends_on": ["TEST-01"],
    "action": "执行事务一致性测试：模拟 SaveChangesAsync 抛出异常，断言练习记录和操作日志均未写入数据库。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AddPracticeAsync",
    "notes": ""
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03", "TEST-04"],
    "action": "在 Release 配置下编译解决方案并运行所有相关测试。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~AddPractice",
    "notes": "必须零警告，无测试失败。"
  }
]
```