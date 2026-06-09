# GetAllReferencedImagePaths — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 GetAllReferencedImagePathsResponse proto 定义：确认包含 repeated string image_paths、Empty 消息定义正确。",
    "files": ["src/Contract/Protos/mistake.proto", "src/Contract/Protos/mistake.common.proto"],
    "acceptance": "proto 定义与文档一致；生成代码编译通过 dotnet build。",
    "notes": "Empty 消息来自 mistake.common.proto。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IMistakeItemRepository.GetAllAsync 与 IMistakePracticeRepository.GetAllAsync 接口：确认方法存在、签名正确、返回类型正确。",
    "files": ["src/Domain/Repositories/IMistakeItemRepository.cs", "src/Domain/Repositories/IMistakePracticeRepository.cs"],
    "acceptance": "dotnet build；接口与实现一一对应。",
    "notes": "GetAllAsync 返回全量数据——数据量大时需关注性能和内存。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeDomainService.GetAllReferencedImagePathsAsync 方法：确认使用 HashSet<StringComparer.OrdinalIgnoreCase> 去重；确认遍历 SourceRegions 和 ImagePaths 收集路径；确认过滤 IsNullOrWhiteSpace。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；代码逻辑与 SPEC FR-01/FR-02/FR-03/FR-04 一致。",
    "notes": "大小写不敏感去重是关键——确认使用 OrdinalIgnoreCase 而非 CurrentCultureIgnoreCase。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.GetAllReferencedImagePaths 方法：确认无参数、调用领域服务、将 HashSet 用 AddRange 添加到响应。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；gRPC 层透传领域服务结果。",
    "notes": "HashSet.AddRange 可能不适用于 proto repeated 字段——检查是否使用 response.ImagePaths.AddRange(paths)。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04"],
    "action": "执行路径收集成功测试：Mock _mistakeItemRepository 返回带有 SourceImagePath 的条目、Mock _mistakePracticeRepository 返回带有 ImagePaths 的练习，调用 GetAllReferencedImagePathsAsync，断言返回路径包含所有非空白路径。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetAllReferencedImagePaths",
    "notes": "验证两种来源的路径都被收集。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行去重测试：Mock 返回包含重复路径（大小写不同）的数据，调用 GetAllReferencedImagePathsAsync，断言结果中去重正确（无重复）。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetAllReferencedImagePaths_Deduplication",
    "notes": "验证 OrdinalIgnoreCase 去重生效。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行跨来源去重测试：Mock 错题条目和错题练习包含相同路径，调用 GetAllReferencedImagePathsAsync，断言该路径在结果中仅出现一次。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetAllReferencedImagePaths_CrossSourceDeduplication",
    "notes": "验证跨 MistakeItem 和 MistakePractice 的去重。"
  },
  {
    "id": "TEST-04",
    "depends_on": ["TEST-01"],
    "action": "执行空路径过滤测试：Mock 返回包含空字符串和空白字符串路径的数据，调用 GetAllReferencedImagePathsAsync，断言空白路径未被包含。",
    "files": ["test/Domain/Services/MistakeDomainServiceTests.cs"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetAllReferencedImagePaths_EmptyFilter",
    "notes": "验证 IsNullOrWhiteSpace 过滤。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03", "TEST-04"],
    "action": "在 Release 配置下编译解决方案并运行所有 GetAllReferencedImagePaths 相关测试，打印覆盖率摘要。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~GetAllReferencedImagePaths",
    "notes": "必须零警告，无测试失败。"
  }
]
```