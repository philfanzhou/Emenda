# 完成上传审核 — 任务清单 (TASKS)

> 本功能已实现完成。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "实现 CompleteUploadReviewAsync 领域服务方法",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "存在 PendingReview 时返回错误，无记录时返回错误，全部审核完成时返回成功"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "实现 CompleteUploadReview gRPC handler 及图片迁移逻辑",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "Confirmed 错题图片迁移到 mistakes/，非 Confirmed 仅移除记录，pathMapping 缓存生效"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "实现 CountPendingBySourceUploadIdAsync 仓储方法",
    "files": ["src/Database/Repositories/MistakeItemRepository.cs"],
    "acceptance": "正确统计指定 sourceUploadId 下 PendingReview 状态的错题数量"
  },
  {
    "id": "TASK-04",
    "status": "implemented",
    "depends_on": [],
    "action": "实现 CompleteUploadReviewResult 领域模型",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "包含 Success/ErrorMessage/SourceUploadId/StudentId/Items 字段"
  },
  {
    "id": "TASK-05",
    "status": "implemented",
    "depends_on": [],
    "action": "补充 Domain Service 单元测试",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs"],
    "acceptance": "覆盖 PendingItemsExist/AllReviewed/NoItems 三个场景"
  },
  {
    "id": "TASK-06",
    "status": "implemented",
    "depends_on": [],
    "action": "补充图片路径迁移单元测试",
    "files": ["test/Ruoyu.Study.Mistake.Domain.Tests/ImagePathMigrationTests.cs"],
    "acceptance": "覆盖路径替换/缓存复用/已迁移跳过/空路径跳过/多图片 OSS 操作"
  },
  {
    "id": "TASK-07",
    "status": "to_review",
    "depends_on": ["TASK-02"],
    "action": "OSS 迁移失败时的容错行为验证",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "单个图片迁移失败不阻塞其他图片，记录 Error 日志"
  }
]
```
