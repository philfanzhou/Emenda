# 按上传记录查询错题 — 任务清单 (TASKS)

> 本功能已实现完成。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "实现 GetMistakeItemsByUpload gRPC handler",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "传入 sourceUploadId 返回 MistakeItemListResponse"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "实现 GetMistakeItemsByUploadAsync 领域服务方法",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "透传 Repository.GetBySourceUploadIdAsync 结果"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": ["TASK-02"],
    "action": "实现 GetBySourceUploadIdAsync 仓储方法（含客户端排序）",
    "files": ["src/Database/Repositories/MistakeItemRepository.cs"],
    "acceptance": "SQLite 查询后在客户端按 CreatedAt 升序排序"
  },
  {
    "id": "TASK-04",
    "status": "to_fix",
    "depends_on": [],
    "action": "补充单元测试",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/"],
    "acceptance": "覆盖有数据返回列表、无数据返回空列表、排序正确性"
  }
]
```
