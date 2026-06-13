# 图片路径查询 - 任务清单

```json
[
  {
    "id": "TIP-01",
    "title": "Proto 定义 GetAllReferencedImagePaths RPC 及消息类型",
    "status": "implemented",
    "notes": "mistake.proto 中已定义 rpc GetAllReferencedImagePaths(Empty) returns (GetAllReferencedImagePathsResponse)"
  },
  {
    "id": "TIP-02",
    "title": "IMistakeItemRepository.GetAllAsync 接口定义与实现",
    "status": "implemented",
    "notes": "IMistakeItemRepository.cs 中已定义，MistakeItemRepository.cs 中已实现"
  },
  {
    "id": "TIP-03",
    "title": "IMistakePracticeRepository.GetAllAsync 接口定义与实现",
    "status": "implemented",
    "notes": "IMistakePracticeRepository.cs 中已定义，MistakePracticeRepository.cs 中已实现"
  },
  {
    "id": "TIP-04",
    "title": "MistakeDomainService.GetAllReferencedImagePathsAsync 领域服务",
    "status": "implemented",
    "notes": "MistakeDomainService.cs 中已实现，含去重和空值过滤逻辑"
  },
  {
    "id": "TIP-05",
    "title": "MistakeServiceImpl.GetAllReferencedImagePaths gRPC 端点",
    "status": "implemented",
    "notes": "MistakeServiceImpl.cs 中已实现"
  },
  {
    "id": "TIP-06",
    "title": "单元测试：GetAllReferencedImagePathsAsync 正常返回所有路径",
    "status": "implemented",
    "notes": "MistakeDomainServiceTests.GetAllReferencedImagePathsAsync_ShouldReturnAllPaths"
  },
  {
    "id": "TIP-07",
    "title": "单元测试：大小写不敏感去重",
    "status": "todo",
    "notes": "无测试覆盖"
  },
  {
    "id": "TIP-08",
    "title": "单元测试：空值和空白字符串过滤",
    "status": "todo",
    "notes": "无测试覆盖"
  },
  {
    "id": "TIP-09",
    "title": "单元测试：无图片时返回空集合",
    "status": "todo",
    "notes": "无测试覆盖"
  },
  {
    "id": "TIP-10",
    "title": "性能优化：大数据量场景下的全表扫描问题",
    "status": "todo",
    "notes": "当前 GetAllAsync 加载全表到内存，数据量大时可能需要改为数据库层聚合查询"
  }
]
```
