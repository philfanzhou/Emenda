# AnalyzeMistakeType — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 MistakeTypeResponse 与 AnalyzeMistakeRequest proto 定义：确认 type/type_name/analysis_reason 字段定义与 SPEC 一致；确认 AnalyzeMistakeRequest 包含 content/correct_answer/student_answer 预留字段。",
    "files": ["src/Contract/Protos/mistake.proto"],
    "acceptance": "代码阅读与 proto 定义一致；编译通过 dotnet build。",
    "notes": "预留字段 content/correct_answer/student_answer 当前未使用，未来用于 AI 分析实装。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 MistakeDomainService.AnalyzeMistakeTypeAsync 方法：确认硬编码返回 type=5/typeName='其他错误'/analysisReason='根据答案对比无法明确分类'，确认错题存在时记录 ProcessAction.Analyzed 日志、不存在时跳过。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；代码逻辑与 SPEC 一致。",
    "notes": "当前为占位实现，未来应替换为 AI 分析逻辑——需保持接口签名不变。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.AnalyzeMistakeType 方法：确认仅传递 MistakeItemId 给领域服务（忽略 content/correct_answer/student_answer），确认返回 MistakeTypeResponse 未做额外映射。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；gRPC 层透传领域服务结果。",
    "notes": "gRPC 层不额外处理请求中的 content/correct_answer/student_answer 字段。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03"],
    "action": "执行分析成功测试：使用存在的错题条目 ID 调用 AnalyzeMistakeTypeAsync，断言返回 type=5/typeName='其他错误'，断言 _processLogRepository.AddAsync 被调用一次且 Action=Analyzed，断言 _unitOfWork.SaveChangesAsync 被调用。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AnalyzeMistakeType",
    "notes": "验证硬编码分析结果的返回和日志记录行为。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行错题不存在测试：使用不存在的错题条目 ID 调用 AnalyzeMistakeTypeAsync，断言仍返回分析结果但 _processLogRepository.AddAsync 不被调用、_unitOfWork.SaveChangesAsync 不被调用。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~AnalyzeMistakeType_ItemNotExists",
    "notes": "验证错题不存在时静默跳过日志记录。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02"],
    "action": "在 Release 配置下编译解决方案并运行所有 AnalyzeMistakeType 相关测试，打印覆盖率摘要。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~AnalyzeMistakeType",
    "notes": "必须零警告，无测试失败。"
  }
]
```