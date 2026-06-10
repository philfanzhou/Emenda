# 错题列表查询 — 任务清单 (TASKS)

> 本功能已实现完成。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "实现 GetMistakeItemList gRPC handler",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "调用 GetMistakeItemList RPC 返回正确分页结果"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "实现 Domain Service 参数校验和分页修正",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "page=0 时修正为 1，size=200 时修正为 100"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": ["TASK-02"],
    "action": "实现 Repository 多条件筛选查询",
    "files": ["src/Database/Repositories/MistakeItemRepository.cs"],
    "acceptance": "按 studentId+subject 筛选返回正确结果"
  },
  {
    "id": "TASK-04",
    "status": "to_fix",
    "depends_on": [],
    "action": "补充单元测试覆盖分页修正逻辑",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/"],
    "acceptance": "dotnet test --filter GetMistakeItemList"
  }
]
```