# GetPendingReviewUploads — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 PendingReviewUploadModel 模型字段：确认 SourceUploadId/StudentId/Subject/Grade/PendingCount/TotalCount/EarliestCreatedAt 的类型、命名与 proto PendingReviewUploadDto 字段一一对应。",
    "files": ["src/Domain/Models/MistakeItemModel.cs"],
    "acceptance": "编译通过 dotnet build；模型字段与 proto 消息字段语义一致。",
    "notes": "EarliestCreatedAt 使用 DateTimeOffset，注意与 proto string 时间格式的映射。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 IMistakeItemRepository.cs 中 GetPendingReviewUploadsAsync 接口签名：确认返回值为 (List<PendingReviewUploadModel>, int TotalCount) 元组，subjects 参数为 optional List<int>?。",
    "files": ["src/Domain/Repositories/IMistakeItemRepository.cs"],
    "acceptance": "编译通过；接口与 MistakeItemRepository 实现一一对应。",
    "notes": "元组返回值语义：Item1 为当前页数据，Item2 为总记录数。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeItemRepository.cs 中 GetPendingReviewUploadsAsync 实现：确认 SQL/EF 查询正确筛选 PendingReview 记录、按 source_upload_id 分组、统计 pending_count/total_count、支持 subjects 过滤、分页逻辑正确。",
    "files": ["src/Database/Repositories/MistakeItemRepository.cs"],
    "acceptance": "编译通过；聚合 SQL 无 N+1 问题。",
    "notes": "应确认仅返回至少有一条 PendingReview 记录的上传分组。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 GetPendingReviewUploadsAsync 方法：确认分页参数修正逻辑（page≤0→1, size≤0→10, size>100→100）、subjects 透传、返回值直接透传仓储结果。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；分页修正逻辑覆盖所有边界。",
    "notes": "分页修正为静默修正，不抛异常。"
  },
  {
    "id": "REVIEW-05",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 GetPendingReviewUploads 方法：确认 gRPC 层处理 subjects 过滤（request.Subjects 非空时才构建 List）、构建 PendingReviewUploadDto 映射、EarliestCreatedAt 为 MinValue 时输出空字符串、PageMeta 计算正确。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；DTO 映射字段完整无遗漏。",
    "notes": "total_pages 使用 (totalCount + size - 1) / size 向上取整。"
  },
  {
    "id": "REVIEW-06",
    "depends_on": [],
    "action": "评审 mistake.proto 与 mistake.common.proto 中 GetPendingReviewUploads 相关消息定义：确认 GetPendingReviewUploadsRequest 含 page/size/subjects 字段、PendingReviewUploadListResponse 含 uploads/page_meta、PendingReviewUploadDto 字段完整。",
    "files": ["src/Contract/Protos/mistake.proto", "src/Contract/Protos/mistake.common.proto"],
    "acceptance": "proto 编译通过；字段编号与顺序一致。",
    "notes": "subjects 为 repeated int32，默认空列表表示不过滤。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04", "REVIEW-05", "REVIEW-06"],
    "action": "执行分页查询主流程测试：Mock 仓储返回 2 条 PendingReviewUploadModel 和 totalCount=2，调用 GetPendingReviewUploadsAsync(page=1, size=10)，断言返回列表 Count=2、totalCount=2。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetPendingReviewUploadsAsync_ReturnsPaginated",
    "notes": "验证仓储结果正确透传。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行分页参数修正测试：传入 page=0、size=0，断言自动修正为 page=1、size=10；传入 size=200，断言修正为 size=100。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetPendingReviewUploadsAsync_PageParamCorrection",
    "notes": "分页修正逻辑在领域服务中执行，可纯单元测试。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行 subjects 过滤测试：传入 subjects=[1,2]，断言仓储收到的 subjects 参数为 [1,2]；传入 subjects 为空列表时不传 subjects 参数。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetPendingReviewUploadsAsync_SubjectFilter",
    "notes": "Moq 验证仓储方法调用参数。"
  },
  {
    "id": "TEST-04",
    "depends_on": ["TEST-01"],
    "action": "执行空结果测试：Mock 仓储返回空列表和 totalCount=0，断言返回空列表、totalCount=0。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetPendingReviewUploadsAsync_EmptyResult",
    "notes": "空结果不是错误场景，正常返回。"
  },
  {
    "id": "TEST-05",
    "depends_on": ["TEST-01"],
    "action": "执行 gRPC 层 DTO 映射测试：Mock 领域服务返回包含 EarliestCreatedAt=DateTimeOffset.MinValue 的记录，断言输出 earliest_created_at 为空字符串。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~GetPendingReviewUploads_MinValueDateTime",
    "notes": "DateTimeOffset.MinValue 映射为空字符串而非 0001-01-01。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03", "TEST-04", "TEST-05"],
    "action": "在 Release 配置下编译解决方案并运行所有 GetPendingReviewUploads 相关测试。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~GetPendingReviewUploads",
    "notes": "必须零警告，无测试失败。"
  }
]
```