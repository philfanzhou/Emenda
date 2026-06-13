# 错题条目查询 — 任务清单 (TASKS)

> 本功能已实现完成。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "实现 GetMistakeItem gRPC handler",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "传入有效 ID 返回 MistakeItemDto，传入不存在 ID 返回 NotFound"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "实现 GetMistakeItemAsync 领域服务方法",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "透传 Repository.GetByIdAsync 结果"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": ["TASK-02"],
    "action": "实现 GetByIdAsync 仓储方法",
    "files": ["src/Database/Repositories/MistakeItemRepository.cs"],
    "acceptance": "使用 FindAsync 主键查询，返回 MistakeItemModel 或 null"
  },
  {
    "id": "TASK-04",
    "status": "to_fix",
    "depends_on": [],
    "action": "补充单元测试",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/"],
    "acceptance": "覆盖有效 ID 返回数据、不存在 ID 返回 null 两个场景"
  }
]
```
