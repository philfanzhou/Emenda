```json
[
  {
    "id": "T-01",
    "title": "Proto 定义 IdRequest / MistakePracticeListResponse",
    "status": "implemented"
  },
  {
    "id": "T-02",
    "title": "gRPC 层 GetMistakePractices 方法实现",
    "status": "implemented"
  },
  {
    "id": "T-03",
    "title": "领域服务 GetPracticesAsync 实现",
    "status": "implemented"
  },
  {
    "id": "T-04",
    "title": "MistakePracticeRepository.GetByMistakeItemIdAsync 实现（含排序）",
    "status": "implemented"
  },
  {
    "id": "T-05",
    "title": "MapPracticeDto 映射方法",
    "status": "implemented"
  },
  {
    "id": "T-06",
    "title": "单元测试：GetPracticesAsync 返回正确数据",
    "status": "todo"
  },
  {
    "id": "T-07",
    "title": "单元测试：无练习记录时返回空列表",
    "status": "todo"
  },
  {
    "id": "T-08",
    "title": "单元测试：结果按 PracticeDate 升序排列",
    "status": "todo"
  },
  {
    "id": "T-09",
    "title": "集成测试：gRPC 层 GetMistakePractices 端到端验证",
    "status": "todo"
  }
]
```
