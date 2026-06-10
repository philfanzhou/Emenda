# 错题类型分析 - 任务清单

```json
[
  {
    "id": "TAT-01",
    "title": "Proto 定义 AnalyzeMistakeType RPC 及消息类型",
    "status": "implemented",
    "notes": "mistake.proto 中已定义 AnalyzeMistakeRequest, MistakeTypeResponse"
  },
  {
    "id": "TAT-02",
    "title": "MistakeDomainService.AnalyzeMistakeTypeAsync 硬编码实现",
    "status": "implemented",
    "notes": "返回 type=5, typeName='其他错误', analysisReason='根据答案对比无法明确分类'"
  },
  {
    "id": "TAT-03",
    "title": "错题存在时记录 ProcessLog (Action=Analyzed)",
    "status": "implemented",
    "notes": "MistakeDomainService.cs 中已实现条件性日志记录"
  },
  {
    "id": "TAT-04",
    "title": "MistakeServiceImpl.AnalyzeMistakeType gRPC 端点",
    "status": "implemented",
    "notes": "MistakeServiceImpl.cs 中已实现，但 content/correct_answer/student_answer 未传入"
  },
  {
    "id": "TAT-05",
    "title": "Guid.Parse 异常处理（无效 ID 格式）",
    "status": "to_fix",
    "notes": "当前 Guid.Parse 无保护，无效格式会抛出 FormatException"
  },
  {
    "id": "TAT-06",
    "title": "领域服务返回类型解耦（当前返回 gRPC MistakeTypeResponse）",
    "status": "to_review",
    "notes": "领域服务直接返回 Contract 层类型，违反分层原则，应改为领域模型"
  },
  {
    "id": "TAT-07",
    "title": "真正的错题类型分析逻辑实现",
    "status": "todo",
    "notes": "核心待实现功能：利用 content/correct_answer/student_answer 进行智能分类"
  },
  {
    "id": "TAT-08",
    "title": "定义完整的错误类型分类体系",
    "status": "todo",
    "notes": "需要与业务方确认分类标准（如：计算错误、概念错误、审题错误等）"
  },
  {
    "id": "TAT-09",
    "title": "gRPC 层传递 content/correct_answer/student_answer 到领域服务",
    "status": "todo",
    "notes": "当前 gRPC 层忽略了这些参数，待分析逻辑实现后需传入"
  },
  {
    "id": "TAT-10",
    "title": "单元测试：AnalyzeMistakeTypeAsync 错题存在时记录日志",
    "status": "todo",
    "notes": "无测试覆盖"
  },
  {
    "id": "TAT-11",
    "title": "单元测试：AnalyzeMistakeTypeAsync 错题不存在时不记录日志",
    "status": "todo",
    "notes": "无测试覆盖"
  },
  {
    "id": "TAT-12",
    "title": "单元测试：AnalyzeMistakeTypeAsync 返回硬编码值",
    "status": "todo",
    "notes": "无测试覆盖"
  }
]
```
