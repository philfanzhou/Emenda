# ReviewMistakeItem — 任务清单 (TASKS)

> 说明：本功能代码已实现完成，下列任务为 **代码评审与自动化验证** 任务。

```json
[
  {
    "id": "REVIEW-01",
    "depends_on": [],
    "action": "评审 MistakeItemModel.cs 中审核相关字段：确认 ReviewStatus/ReviewerId/ReviewedAt/ReviewComment/UpdatedAt 的类型、命名与注释与 SPEC 一致；确认 Grade 与 Subject 为 int 类型且与 proto ReviewMistakeItemRequest 对齐。",
    "files": ["src/Domain/Models/MistakeItemModel.cs"],
    "acceptance": "编译通过 dotnet build；字段语义与 ReviewMistakeItemRequest proto 消息一致。",
    "notes": "ReviewStatus 使用 int 存储而非枚举，需确认映射正确。"
  },
  {
    "id": "REVIEW-02",
    "depends_on": [],
    "action": "评审 MistakeServiceImpl.cs 中 ReviewMistakeItem 方法：确认 gRPC 层前置校验 review_status 为 Confirmed 或 Rejected 并抛出 InvalidArgument；确认 grade/subject 可选参数（>0 才透传）逻辑正确。",
    "files": ["src/Service/MistakeServiceImpl.cs"],
    "acceptance": "编译通过；InvalidArgument 场景覆盖 Confirmed/Rejected 以外的所有 ReviewStatus 值。",
    "notes": "gRPC 层校验与领域服务层双重校验，防御性编程。"
  },
  {
    "id": "REVIEW-03",
    "depends_on": [],
    "action": "评审 MistakeDomainService.cs 中 ReviewMistakeItemAsync 方法：确认错题不存在返回 null、状态非 PendingReview 抛出 MistakeValidationException、审核值二次校验、grade/subject 可选更新逻辑、操作日志记录（ReviewConfirmed/ReviewRejected）、事务提交。",
    "files": ["src/Domain/Services/MistakeDomainService.cs"],
    "acceptance": "编译通过；所有状态转换路径与 SPEC 一致。",
    "notes": "grade 校验上限 12，subject 校验上限 9。"
  },
  {
    "id": "REVIEW-04",
    "depends_on": [],
    "action": "评审 mistake.proto 中 ReviewMistakeItemRequest 消息定义：确认 id/review_status/reviewer_id/review_comment/grade/subject 字段存在且类型正确。",
    "files": ["src/Contract/Protos/mistake.proto", "src/Contract/Protos/mistake.common.proto"],
    "acceptance": "proto 编译通过；ReviewStatus 枚举值（Confirmed=2, Rejected=3）与代码中 int 值一致。",
    "notes": "grade 和 subject 为 optional 语义，proto3 中通过值是否为默认 0 判断。"
  },
  {
    "id": "TEST-01",
    "depends_on": ["REVIEW-01", "REVIEW-02", "REVIEW-03", "REVIEW-04"],
    "action": "执行审核主流程测试：对 PendingReview 状态的错题执行确认审核，断言返回模型非 null、ReviewStatus==2、ReviewerId/ReviewComment 一致、ReviewedAt/UpdatedAt 接近当前时间（±5s）、操作日志记录 action==ReviewConfirmed。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ReviewMistakeItemAsync_Confirm",
    "notes": "Mock 仓储返回 PendingReview 状态错题；Mock IUnitOfWork 确认 SaveChangesAsync 被调用。"
  },
  {
    "id": "TEST-02",
    "depends_on": ["TEST-01"],
    "action": "执行驳回审核测试：对 PendingReview 错题执行驳回，断言 ReviewStatus==3、操作日志 action==ReviewRejected。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ReviewMistakeItemAsync_Reject",
    "notes": "验证驳回与确认使用不同的 ProcessAction。"
  },
  {
    "id": "TEST-03",
    "depends_on": ["TEST-01"],
    "action": "执行可选字段更新测试：传入 grade=5 和 subject=3，断言模型 Grade==5、Subject==3；传入 grade=0 和 subject=0，断言 Grade 和 Subject 保持不变。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ReviewMistakeItemAsync_OptionalFields",
    "notes": "grade/subject 为 0 时应跳过更新。"
  },
  {
    "id": "TEST-04",
    "depends_on": ["TEST-01"],
    "action": "执行状态校验测试：传入已确认或已驳回的错题 ID，断言抛出 MistakeValidationException，消息包含"只能审核待审核状态的错题"。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ReviewMistakeItemAsync_InvalidStatus",
    "notes": "Mock 仓储返回 ReviewStatus!=PendingReview 的错题模型。"
  },
  {
    "id": "TEST-05",
    "depends_on": ["TEST-01"],
    "action": "执行 gRPC 层校验测试：通过 gRPC 传入 ReviewStatus.Unspecified 和 ReviewStatus.PendingReview，断言均抛出 RpcException(StatusCode.InvalidArgument)，消息包含"审核结果只能是确认或驳回"。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ReviewMistakeItem_InvalidReviewStatus",
    "notes": "gRPC 层拦截非法值，不进入领域服务。"
  },
  {
    "id": "TEST-06",
    "depends_on": ["TEST-01"],
    "action": "执行错题不存在测试：传入不存在的错题 ID，断言领域服务返回 null，gRPC 返回 BoolResponse{Success=false}。",
    "files": ["test/"],
    "acceptance": "dotnet test --filter FullyQualifiedName~ReviewMistakeItemAsync_NotFound",
    "notes": "Mock 仓储 GetByIdAsync 返回 null。"
  },
  {
    "id": "BUILD-01",
    "depends_on": ["TEST-01", "TEST-02", "TEST-03", "TEST-04", "TEST-05", "TEST-06"],
    "action": "在 Release 配置下编译解决方案并运行所有 ReviewMistakeItem 相关测试。",
    "files": ["backend/ruoyu.mistake/*.sln", "backend/ruoyu.mistake/src/**/*.cs", "backend/ruoyu.mistake/test/**/*.cs"],
    "acceptance": "dotnet test --configuration Release --filter FullyQualifiedName~ReviewMistakeItem",
    "notes": "必须零警告，无测试失败。"
  }
]
```