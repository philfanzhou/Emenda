# 错题处理历史查询 - 任务清单

```json
[
  {
    "id": "TPL-01",
    "title": "Proto 定义 GetMistakeProcessHistory RPC",
    "status": "implemented",
    "notes": "mistake.proto 中已定义 rpc GetMistakeProcessHistory(IdRequest) returns (ProcessLogListResponse)"
  },
  {
    "id": "TPL-02",
    "title": "IProcessLogRepository.GetByMistakeItemIdAsync 接口定义",
    "status": "implemented",
    "notes": "IProcessLogRepository.cs 中已定义"
  },
  {
    "id": "TPL-03",
    "title": "ProcessLogRepository 仓储实现（含 OrderBy CreatedAt 升序）",
    "status": "implemented",
    "notes": "ProcessLogRepository.cs 中已实现，OrderBy(p => p.CreatedAt)"
  },
  {
    "id": "TPL-04",
    "title": "MistakeDomainService.GetMistakeProcessHistoryAsync 领域服务",
    "status": "implemented",
    "notes": "MistakeDomainService.cs 中已实现，直接委托仓储"
  },
  {
    "id": "TPL-05",
    "title": "MistakeServiceImpl.GetMistakeProcessHistory gRPC 端点",
    "status": "implemented",
    "notes": "MistakeServiceImpl.cs 中已实现，含 MapProcessLogDto 映射"
  },
  {
    "id": "TPL-06",
    "title": "Guid.Parse 异常处理（无效 ID 格式）",
    "status": "to_fix",
    "notes": "当前 Guid.Parse 无保护，无效格式会抛出 FormatException，应改为友好错误"
  },
  {
    "id": "TPL-07",
    "title": "单元测试：GetMistakeProcessHistoryAsync 正常返回",
    "status": "todo",
    "notes": "无测试覆盖"
  },
  {
    "id": "TPL-08",
    "title": "单元测试：GetMistakeProcessHistoryAsync 空结果",
    "status": "todo",
    "notes": "无测试覆盖"
  },
  {
    "id": "TPL-09",
    "title": "单元测试：gRPC 层 Guid.Parse 无效格式处理",
    "status": "todo",
    "notes": "无测试覆盖"
  }
]
```
