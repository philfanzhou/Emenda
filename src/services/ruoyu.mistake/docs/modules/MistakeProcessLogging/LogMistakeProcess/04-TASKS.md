```json
[
  {
    "id": "T-01",
    "title": "Proto 定义 LogMistakeProcessRequest / BoolResponse",
    "status": "implemented"
  },
  {
    "id": "T-02",
    "title": "gRPC 层 LogMistakeProcess 方法实现",
    "status": "implemented"
  },
  {
    "id": "T-03",
    "title": "领域服务 LogMistakeProcessAsync 实现（验证 + 创建日志）",
    "status": "implemented"
  },
  {
    "id": "T-04",
    "title": "AddProcessLogAsync 辅助方法实现",
    "status": "implemented"
  },
  {
    "id": "T-05",
    "title": "ProcessLogRepository.AddAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-06",
    "title": "单元测试：LogMistakeProcessAsync 错题存在时返回 true",
    "status": "todo"
  },
  {
    "id": "T-07",
    "title": "单元测试：LogMistakeProcessAsync 错题不存在时返回 false",
    "status": "todo"
  },
  {
    "id": "T-08",
    "title": "单元测试：验证 ProcessLog 的 Action 和 Details 正确记录",
    "status": "todo"
  },
  {
    "id": "T-09",
    "title": "单元测试：验证 ProcessLog 的 CreatedAt 为 UtcNow",
    "status": "todo"
  },
  {
    "id": "T-10",
    "title": "集成测试：gRPC 层 LogMistakeProcess 端到端验证",
    "status": "todo"
  }
]
```
