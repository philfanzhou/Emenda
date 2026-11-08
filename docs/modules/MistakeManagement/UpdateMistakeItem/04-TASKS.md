```json
[
  {
    "id": "T-01",
    "title": "Proto 定义 UpdateMistakeItemRequest",
    "status": "implemented"
  },
  {
    "id": "T-02",
    "title": "gRPC 层 UpdateMistakeItem 方法实现（查询 + 部分更新 + 错误处理）",
    "status": "implemented"
  },
  {
    "id": "T-03",
    "title": "领域服务 UpdateMistakeItemAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-04",
    "title": "仓储层 UpdateAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-05",
    "title": "单元测试 UpdateMistakeItemAsync_ShouldUpdateItem",
    "status": "implemented"
  },
  {
    "id": "T-06",
    "title": "单元测试：错题不存在时返回 NotFound",
    "status": "todo"
  },
  {
    "id": "T-07",
    "title": "单元测试：部分更新仅修改非空字段",
    "status": "todo"
  },
  {
    "id": "T-08",
    "title": "单元测试：更新失败返回 Internal 错误",
    "status": "todo"
  },
  {
    "id": "T-09",
    "title": "单元测试：MistakeValidationException 转换为 InvalidArgument",
    "status": "todo"
  },
  {
    "id": "T-10",
    "title": "集成测试：gRPC 层 UpdateMistakeItem 端到端验证",
    "status": "todo"
  }
]
```
