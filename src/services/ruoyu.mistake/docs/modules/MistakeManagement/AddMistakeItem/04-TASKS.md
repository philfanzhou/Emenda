```json
[
  {
    "id": "T-01",
    "title": "Proto 定义 AddMistakeItemRequest / MistakeItemDto",
    "status": "implemented"
  },
  {
    "id": "T-02",
    "title": "gRPC 层 AddMistakeItem 方法实现（请求映射 + 错误转换）",
    "status": "implemented"
  },
  {
    "id": "T-03",
    "title": "领域服务 AddMistakeItemAsync 实现（验证 + 默认值 + ProcessLog）",
    "status": "implemented"
  },
  {
    "id": "T-04",
    "title": "ValidateMistakeItemData 验证逻辑",
    "status": "implemented"
  },
  {
    "id": "T-05",
    "title": "仓储层 AddAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-06",
    "title": "MapMistakeItemDto 映射方法",
    "status": "implemented"
  },
  {
    "id": "T-07",
    "title": "单元测试 AddMistakeItemAsync_WithSourceRegions_ShouldSucceed",
    "status": "implemented"
  },
  {
    "id": "T-08",
    "title": "单元测试：验证失败场景（缺少 studentId / subject 越界 / grade 越界 / 缺少 sourceUploadId / 缺少 sourceRegions）",
    "status": "todo"
  },
  {
    "id": "T-09",
    "title": "集成测试：gRPC 层 AddMistakeItem 端到端验证",
    "status": "todo"
  }
]
```
