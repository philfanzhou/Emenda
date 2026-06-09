# GetMistakeProcessHistory — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 ProcessLogDto proto 定义与 ProcessLogListResponse：确认 ProcessLogDto 包含 id/mistake_item_id/action/details/created_at 字段；确认 ProcessLogListResponse 包含 repeated ProcessLogDto logs。",
    "files": ["src/Contract/Protos/mistake.common.proto", "src/Contract/Protos/mistake.proto"],
    "acceptance": "proto 定义与文档一致；生成代码编译通过 dotnet build。",
    "notes": "ProcessLogDto 的 created_at 为 string 格式（ISO 8601）。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IProcessLogRepository.GetByMistakeItemIdAsync 接口签名与 ProcessLogRepository 实现：确认方法存在、签名正确、返回 List<ProcessLogModel>。",
    "files": ["src/Domain/Repositories/IProcessLogRepository.cs", "src/Database/Repositories/ProcessLogRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "确认仓储实现按 created_at 升序排列（通常使用 OrderBy(log => log.CreatedAt)）。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeDomainService.GetMistakeProcessHistoryAsync 方法：确认直接委托给 _processLogRepository.GetByMistakeItemIdAsync，无额外逻辑。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；代码逻辑为简单委托。",
    "notes": "方法仅做透传，无空值检查、无排序逻辑（由仓储层保证）。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.GetMistakeProcessHistory 与 MapProcessLogDto 方法：确认 Guid.Parse 解析 Id、调用领域服务、使用 MapProcessLogDto 映射每个模型到 DTO。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；映射逻辑正确——Dt >= DateTimeOffset.MinValue 时格式化为 ISO 8601。",
    "notes": "ProcessLogDto.created_at 映射时处理 MinValue 边界情况（返回空字符串）。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04"],
    "action": "执行查询成功测试：Mock _processLogRepository 返回多条日志，调用 GetMistakeProcessHistoryAsync，断言返回的日志数量与 Mock 一致，断言每条的字段与原始数据一致。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetMistakeProcessHistory",
    "notes": "验证领域服务正确委托给仓储。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行空列表测试：Mock _processLogRepository 返回空列表，调用 GetMistakeProcessHistoryAsync，断言返回空列表。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetMistakeProcessHistory_Empty",
    "notes": "验证无日志时返回空列表。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02"],
    "action": "在 Release 配置下编译解决方案并运行所有 GetMistakeProcessHistory 相关测试，打印覆盖率摘要。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~GetMistakeProcessHistory",
    "notes": "必须零警告，无测试失败。"
  }
]
```