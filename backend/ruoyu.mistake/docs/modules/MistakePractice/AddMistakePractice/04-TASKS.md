```json
[
  {
    "id": "T-01",
    "title": "Proto 定义 AddMistakePracticeRequest / MistakePracticeDto",
    "status": "implemented"
  },
  {
    "id": "T-02",
    "title": "gRPC 层 AddMistakePractice 方法实现（映射 + 错误转换）",
    "status": "implemented"
  },
  {
    "id": "T-03",
    "title": "领域服务 AddPracticeAsync 实现（验证 + 创建 + ProcessLog）",
    "status": "implemented"
  },
  {
    "id": "T-04",
    "title": "MistakePracticeRepository.AddAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-05",
    "title": "MapPracticeDto 映射方法",
    "status": "implemented"
  },
  {
    "id": "T-06",
    "title": "单元测试 AddPracticeAsync_WhenItemNotConfirmed_ShouldThrow",
    "status": "implemented"
  },
  {
    "id": "T-07",
    "title": "单元测试 AddPracticeAsync_WhenItemConfirmed_ShouldAddPractice",
    "status": "implemented"
  },
  {
    "id": "T-08",
    "title": "单元测试：错题不存在时抛出 MistakeValidationException",
    "status": "todo"
  },
  {
    "id": "T-09",
    "title": "单元测试：带 imagePaths 参数时正确赋值",
    "status": "todo"
  },
  {
    "id": "T-10",
    "title": "单元测试：imagePaths 为 null 时不修改默认值",
    "status": "todo"
  },
  {
    "id": "T-11",
    "title": "单元测试：ProcessLog 记录 Action=Practiced",
    "status": "todo"
  },
  {
    "id": "T-12",
    "title": "集成测试：gRPC 层 AddMistakePractice 端到端验证",
    "status": "todo"
  }
]
```
