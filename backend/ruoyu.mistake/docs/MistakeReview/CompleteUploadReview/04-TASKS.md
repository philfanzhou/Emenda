# CompleteUploadReview — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 CompleteUploadReviewResult 领域模型：确认 Success/ErrorMessage/SourceUploadId/StudentId/Items 字段完整且类型正确。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过 dotnet build；Result 类字段与 gRPC 层使用一致。",
    "notes": "Items 为 List<MistakeItemModel>，用于 gRPC 层后续 OSS 操作。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 CompleteUploadReviewAsync 方法：确认 pendingCount 校验（>0 返回失败）、items 空校验（==0 返回失败）、返回的 StudentId 取自第一条错题、Items 包含所有错题。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；前置校验逻辑与 SPEC FR-02/FR-03 一致。",
    "notes": "StudentId 取自 items[0].StudentId，隐式假设同一上传记录的错题属同一学生。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 CompleteUploadReview 方法 — 前置校验分支：确认 domainResult.Success==false 时直接返回 CompleteUploadReviewResponse{Success=false, ErrorMessage=...}，不进入 OSS 操作。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；失败场景不会触发 OSS 操作或 Student 服务调用。",
    "notes": "错误消息原样透传到响应中。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 CompleteUploadReview 方法 — OSS 迁移逻辑：确认 uploads/ 路径前缀检查、pathMapping 缓存、CopyObjectAsync→DeleteAsync 顺序、非 uploads/ 路径跳过、Rejected 错题跳过迁移（仅记录日志）、单个失败 catch 不中断、updated 标志后调用 UpdateMistakeItemAsync。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；路径转换逻辑 `mistakes + oldPath.Substring(uploads.Length)` 正确。",
    "notes": "pathMapping 使用 StringComparer.OrdinalIgnoreCase 忽略大小写。"
  },
  {
    "id": "REVIEW-05",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 CompleteUploadReview 方法 — 跨服务调用逻辑：确认 imagePathsToRemoveFromRecord 收集所有错题图片路径（含确认和驳回）、去重（Contains 检查）、调用 RemoveImagesFromRecordAsync、失败时不抛异常仅记录日志。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；imagePathsToRemoveFromRecord 去重逻辑使用 StringComparer.OrdinalIgnoreCase。",
    "notes": "仅 imagesToRemoveFromRecord 非空且 StudentId 非空时才调用 Student 服务。"
  },
  {
    "id": "REVIEW-06",
    "depends_on": [],
    "action": "评审 mistake.proto 中 CompleteUploadReview 相关消息定义：确认 CompleteUploadReviewRequest 含 source_upload_id/reviewer_id、CompleteUploadReviewResponse 含 success/error_message/student_id。",
    "files": ["src/Contract/Protos/mistake.proto"],
    "acceptance": "proto 编译通过；响应字段与代码返回一致。",
    "notes": "reviewer_id 当前未在领域服务中使用，预留用于审计日志。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03"],
    "action": "执行领域服务校验测试 — 存在待审核错题：Mock pendingCount=3，断言返回 Success=false、ErrorMessage 包含 '还有 3 条错题待审核'。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~CompleteUploadReviewAsync_HasPending",
    "notes": "计数检验是核心安全守卫，必须精确校验。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行领域服务校验测试 — 上传记录不存在：Mock items.Count=0，断言返回 Success=false、ErrorMessage='未找到该上传记录的错题'。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~CompleteUploadReviewAsync_NoItems",
    "notes": "请求不存在的上传记录时应有明确错误提示。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行 OSS 迁移主流程测试：Mock 已确认错题图片 uploads/img.png、Mock IOssService 正常执行，断言 CopyObjectAsync 参数 oldPath=uploads/img.png, newPath=mistakes/img.png、DeleteAsync 参数 paths=uploads/img.png、错题 source_regions 路径更新为 mistakes/img.png。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~CompleteUploadReview_MigrateConfirmed",
    "notes": "验证 CopyObjectAsync → DeleteAsync 调用顺序。"
  },
  {
    "id": "TEST-04",
    "depends_on": ["TEST-01"],
    "action": "执行驳回错题跳过迁移测试：Mock 已驳回错题图片 uploads/rejected.png，断言 IOssService.CopyObjectAsync 和 DeleteAsync 均未被调用，source_regions 路径不变。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~CompleteUploadReview_SkipRejected",
    "notes": "Rejected 状态错题的图片不迁移但路径仍会被收集到 imagePathsToRemoveFromRecord。"
  },
  {
    "id": "TEST-05",
    "depends_on": ["TEST-01"],
    "action": "执行路径缓存测试：Mock 两条错题引用相同图片 uploads/shared.png，断言 IOssService.CopyObjectAsync 仅调用一次，两条错题的 source_regions 路径均更新为 mistakes/shared.png。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~CompleteUploadReview_PathMappingCache",
    "notes": "pathMapping 缓存避免重复 OSS 操作。"
  },
  {
    "id": "TEST-06",
    "depends_on": ["TEST-01"],
    "action": "执行 OSS 失败容错测试：Mock IOssService.CopyObjectAsync 抛出异常，断言后续图片继续处理，最终 Success=true，且 _logger.LogError 被调用。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~CompleteUploadReview_OssFailureGraceful",
    "notes": "单张图片失败不应阻塞整个审核完成流程。"
  },
  {
    "id": "TEST-07",
    "depends_on": ["TEST-01"],
    "action": "执行跨服务调用测试：Mock StudentLearningGrpcServiceClient.RemoveImagesFromRecordAsync 正常返回，断言调用参数 RecordId/StudentId/ImagePaths 正确。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~CompleteUploadReview_CallsRemoveImagesFromRecord",
    "notes": "ImagePaths 应收集所有错题的去重图片路径。"
  },
  {
    "id": "TEST-08",
    "depends_on": ["TEST-01"],
    "action": "执行跨服务调用失败容错测试：Mock StudentLearningGrpcServiceClient.RemoveImagesFromRecordAsync 抛出 RpcException，断言整体 Success=true，_logger.LogError 被调用。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~CompleteUploadReview_StudentServiceFailure",
    "notes": "Student 服务调用失败不阻止流程完成。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03", "TEST-04", "TEST-05", "TEST-06", "TEST-07", "TEST-08"],
    "action": "在 Release 配置下编译解决方案并运行所有 CompleteUploadReview 相关测试。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~CompleteUploadReview",
    "notes": "必须零警告，无测试失败。"
  }
]
```