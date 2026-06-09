# LogMistakeProcess — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 ProcessLogModel 领域模型：确认 Id/MistakeItemId/Action/Details/CreatedAt 字段定义与 SPEC 一致；确认 Action 为 int 类型存储 ProcessAction 枚举值。",
    "files": ["src/Domain/Models/ProcessLogModel.cs"],
    "acceptance": "代码阅读字段与文档一致；编译通过 dotnet build。",
    "notes": "Action 使用 int 而非 enum 是为了与数据库整数列兼容。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IProcessLogRepository.AddAsync 接口签名与 ProcessLogRepository 实现：确认方法存在、签名正确。",
    "files": ["src/Domain/Repositories/IProcessLogRepository.cs", "src/Database/Repositories/ProcessLogRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "AddAsync 方法必须在仓储实现中正确映射到 DbContext。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 ProcessLogEntity EF Core 实体：确认表名 process_log、列名映射与 ProcessLogModel 一致。",
    "files": ["src/Database/Entities/ProcessLogEntity.cs"],
    "acceptance": "编译通过；实体字段与数据库列名、约束一致。",
    "notes": "CreatedAt 应使用 timestamptz 类型。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeDomainService.LogMistakeProcessAsync 方法：确认先验证错题条目存在、不存在时返回 false；确认创建 ProcessLogModel 并调用 AddAsync；确认 WriteChangesAsync。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；代码逻辑与 SPEC FR-01/FR-07 一致。",
    "notes": "当前实现未使用 studentId 参数——需确认是否需要存 student_id 到日志模型。"
  },
  {
    "id": "REVIEW-05",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.LogMistakeProcess 方法：确认将 request 各字段正确传递给领域服务，返回 BoolResponse { Success = success }。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；gRPC 层参数透传正确。",
    "notes": "确认 student_id 传入但领域服务不使用——是否需要在日志中记录 student_id。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04", "REVIEW-05"],
    "action": "执行日志记录成功测试：使用存在的错题条目 ID 调用 LogMistakeProcessAsync，断言返回 true；断言 _processLogRepository.AddAsync 被调用一次且模型字段正确；断言 _unitOfWork.SaveChangesAsync 被调用。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~LogMistakeProcess",
    "notes": "验证 ProcessLogModel 的 Id/MistakeItemId/Action/Details/CreatedAt 正确设置。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行错题不存在测试：使用不存在的错题条目 ID 调用 LogMistakeProcessAsync，断言返回 false；断言 _processLogRepository.AddAsync 不被调用；断言 _unitOfWork.SaveChangesAsync 不被调用。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~LogMistakeProcess_ItemNotExists",
    "notes": "验证错题不存在时的快速失败逻辑。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02"],
    "action": "在 Release 配置下编译解决方案并运行所有 LogMistakeProcess 相关测试，打印覆盖率摘要。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~LogMistakeProcess",
    "notes": "必须零警告，无测试失败。"
  }
]
```