# GetMistakePractices — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 IMistakePracticeRepository.cs 中 GetByMistakeItemIdAsync 方法签名：确认参数类型为 Guid、返回类型为 Task<List<MistakePracticeModel>>。",
    "files": ["src/Domain/Repositories/IMistakePracticeRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": ""
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 MistakePracticeRepository.cs 实现：确认 GetByMistakeItemIdAsync 正确过滤 mistake_item_id 并返回所有匹配记录。",
    "files": ["src/Database/Repositories/MistakePracticeRepository.cs"],
    "acceptance": "编译通过；查询条件与索引一致。",
    "notes": "确认未使用 Skip/Take 分页（此接口返回全部练习记录）。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 GetPracticesAsync 方法：确认为简单委托方法，仅调用 _mistakePracticeRepository.GetByMistakeItemIdAsync 并返回结果。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；方法体内无额外业务逻辑。",
    "notes": ""
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 GetMistakePractices 方法：确认使用 Guid.Parse 解析 request.Id、调用 GetPracticesAsync、通过 MapPracticeDto 转换每条记录。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；转换逻辑覆盖 PracticeDate 的 MinValue 边界处理。",
    "notes": "MapPracticeDto 中 PracticeDate 为 DateTimeOffset.MinValue 时返回空字符串。"
  },
  {
    "id": "REVIEW-05",
    "depends_on": [],
    "action": "评审 mistake.proto 中 GetMistakePractices 的 rpc 定义与 IdRequest/MistakePracticeListResponse 消息定义：确认 request/response 类型签名正确。",
    "files": ["src/Contract/Protos/mistake.proto", "src/Contract/Protos/mistake.common.proto"],
    "acceptance": "proto 编译通过；IdRequest.id 用于传递 mistake_item_id。",
    "notes": "IdRequest 是通用消息类型，被 GetMistakeItem、DeleteMistakeItem 等多个接口复用。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04", "REVIEW-05"],
    "action": "执行查询有练习记录测试：mock GetByMistakeItemIdAsync 返回多条练习记录，断言返回的列表 Count 与 mock 一致、每条记录的 MistakeItemId 与入参一致。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetPractices",
    "notes": "验证仓储方法被调用一次。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行查询无练习记录测试：mock GetByMistakeItemIdAsync 返回空列表，断言返回的列表为 Count == 0（非 null）。",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetPractices",
    "notes": "验证空结果场景不抛异常。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02"],
    "action": "在 Release 配置下编译解决方案并运行所有相关测试。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~GetPractices",
    "notes": "必须零警告，无测试失败。"
  }
]
```