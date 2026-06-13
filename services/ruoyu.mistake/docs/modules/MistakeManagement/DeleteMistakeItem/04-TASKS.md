```json
[
  {
    "id": "T-01",
    "title": "Proto 定义 IdRequest / BoolResponse",
    "status": "implemented"
  },
  {
    "id": "T-02",
    "title": "gRPC 层 DeleteMistakeItem 方法实现",
    "status": "implemented"
  },
  {
    "id": "T-03",
    "title": "领域服务 DeleteMistakeItemAsync 实现（查询 + 级联删除 + 事务提交）",
    "status": "implemented"
  },
  {
    "id": "T-04",
    "title": "MistakePracticeRepository.RemoveByMistakeItemIdAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-05",
    "title": "ProcessLogRepository.RemoveByMistakeItemIdAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-06",
    "title": "MistakeItemRepository.DeleteAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-07",
    "title": "单元测试 DeleteMistakeItemAsync_WhenConfirmed_ShouldDelete",
    "status": "implemented"
  },
  {
    "id": "T-08",
    "title": "单元测试 DeleteMistakeItemAsync_WhenPendingReview_ShouldDelete",
    "status": "implemented"
  },
  {
    "id": "T-09",
    "title": "单元测试：错题不存在时返回 false",
    "status": "todo"
  },
  {
    "id": "T-10",
    "title": "单元测试：验证删除顺序（practices → logs → item）",
    "status": "todo"
  },
  {
    "id": "T-11",
    "title": "单元测试：MistakeValidationException 转换为 InvalidArgument",
    "status": "todo"
  },
  {
    "id": "T-12",
    "title": "集成测试：gRPC 层 DeleteMistakeItem 端到端验证",
    "status": "todo"
  }
]
```
