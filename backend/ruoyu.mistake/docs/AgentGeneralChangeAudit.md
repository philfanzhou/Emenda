# Agent General Change Audit

## 1. 本轮任务范围

基于 `TriElementLifecycleAnalysis.md` 文档，对三元素生命周期相关服务进行代码重构，涉及 7 项改动。

## 2. 启动校验结果

- 目标项目路径存在且可访问: ✅ (`backend/ruoyu.mistake`, `backend/ruoyu.student`, `teacher_portal`, `admin_portal`)
- 正式 docs 根目录存在: ✅
- 输入项可解析: ✅ (TriElementLifecycleAnalysis.md 明确定义了 7 项改动)
- 相关模块、文档入口、代码入口已定位: ✅
- 工作区无无关未提交改动: ✅

## 3. 输入项总表

| 编号 | 类型 | 标题 | 关联模块 | 当前状态 |
|------|------|------|----------|----------|
| ITEM-01 | bug | 改动1: ReviewMistakeItem 驳回后删除 MistakeItem 下沉到 gRPC 层 | MistakeServiceImpl | 已完成 |
| ITEM-02 | feature | 改动3: ReviewMistakeItem 增加审核后完整编排 | MistakeServiceImpl, StudentClient | 已完成 |
| ITEM-03 | refactoring | 改动4: 简化 Controller 层，删除跨服务编排代码 | teacher_portal/MistakeController | 已完成 |
| ITEM-04 | refactoring | 改动5: 废弃 CompleteUploadReview | MistakeServiceImpl, MistakeDomainService | 已完成 |
| ITEM-05 | refactoring | 改动6: UploadStatus.Completed→UnderReview 重命名 + Assign后锁定 | student/ImageUploadDomainService, student.proto | 已完成 |
| ITEM-06 | refactoring | 改动7: LegacyClean 标注定位变化 | admin_portal/OssUploadRecordController | 已完成 |

## 4. 每个输入项的处理记录

### ITEM-01: ReviewMistakeItem 驳回后删除 MistakeItem

- **输入摘要**: 驳回审核时，MistakeItem 应在 gRPC 层直接删除，而非由 Controller 层编排
- **代码修改项**:
  - `MistakeServiceImpl.cs`: ReviewMistakeItem 的 Rejected 分支新增 `DeleteMistakeItemAsync` 调用
- **文档修改项**:
  - `modules/MistakeReview/ReviewMistakeItem/01-FEATURE.md`: 补充约束和验收条件
  - `modules/MistakeReview/ReviewMistakeItem/02-SPEC.md`: 新增 FR-11 及 AC-FR-11
  - `modules/MistakeReview/ReviewMistakeItem/03-DESIGN.md`: 数据流和设计决策更新
- **验证结果**: mistake Domain Tests 57 通过, Service Tests 11 通过

### ITEM-02: ReviewMistakeItem 增加审核后完整编排

- **输入摘要**: 审核通过后 gRPC 层直接调 student 服务 RemoveImagesFromRecord；审核后检查剩余 item，无待审核则 MarkUploadRecordAsReturned
- **代码修改项**:
  - `MistakeServiceImpl.cs`: 新增 `_studentClient` 依赖，Confirmed 块末尾新增 RemoveImagesFromRecord，构建 response 后新增编排逻辑
  - `Program.cs`: 注册 StudentLearningGrpcServiceClient 单例
  - `Host.csproj`: 添加 Student.Contract 项目引用
  - `MistakeServiceImplOssMigrationTests.cs`: 新增 _mockStudentClient
- **文档修改项**:
  - `overview/Integration.md`: 新增出向 gRPC 依赖和失败语义
  - `overview/Design.md`: 新增外部依赖图节点
  - `modules/MistakeReview/ReviewMistakeItem/01-FEATURE.md`: 补充编排描述
  - `modules/MistakeReview/ReviewMistakeItem/02-SPEC.md`: 新增 FR-10/12 及验收标准
  - `modules/MistakeReview/ReviewMistakeItem/03-DESIGN.md`: 完整重写数据流和设计决策
- **验证结果**: 构建通过，测试通过

### ITEM-03: 简化 Controller 层

- **输入摘要**: Teacher Controller 的 ReviewItem/BatchReview 不再需要跨服务编排代码；CompleteReview 端点删除
- **代码修改项**:
  - `teacher_portal/MistakeController.cs`: ReviewItem 移除编排代码，BatchReview 移除编排代码，删除 CompleteReview 端点和 CompleteReviewApiRequest 类
- **文档修改项**:
  - `modules/MistakeReview/ReviewMistakeItem/03-DESIGN.md`: 说明编排从 Controller 下沉到 gRPC 层
- **验证结果**: teacher_portal 构建通过

### ITEM-04: 废弃 CompleteUploadReview

- **输入摘要**: 目标流程中不存在独立的 CompleteUploadReview 步骤，标记为 Obsolete
- **代码修改项**:
  - `MistakeServiceImpl.cs`: 方法加 `[Obsolete]`
  - `MistakeDomainService.cs`: 方法加 `[Obsolete]`
  - `IMistakeDomainService.cs`: 接口方法加 `[Obsolete]`
- **文档修改项**:
  - `modules/MistakeReview/CompleteUploadReview/01-FEATURE.md`: 新增废弃声明，状态改为 Deprecated
  - `modules/MistakeReview/CompleteUploadReview/02-SPEC.md`: 新增废弃声明
  - `overview/Integration.md`: 标记为已废弃
  - `overview/Design.md`: 标记为已废弃
  - `development/Troubleshooting.md`: 相关章节添加废弃说明
- **验证结果**: 构建通过

### ITEM-05: UploadStatus.Completed→UnderReview 重命名 + Assign后锁定

- **输入摘要**: 枚举 Completed→UnderReview，Assign 后锁定（禁止删除/移除/追加图片）
- **代码修改项**:
  - `student.proto`: COMPLETED→UNDER_REVIEW, MarkUploadRecordCompleted→MarkUploadRecordUnderReview, AdminCompleteUploadReview→AdminCompleteUploadReviewAsUnderReview
  - `ImageUploadDomainService.cs`: 枚举重命名 + 5个方法新增 UnderReview 锁定
  - `StudentLearningServiceImpl.cs`: 方法名更新
  - `StudentManagementGrpcServiceImpl.cs`: 方法名更新
  - `admin_portal/OssUploadRecordController.cs`: 调用名更新
  - `FingerprintCleanupWorker.cs`: 状态引用更新
  - `UploadAnalysisWorker.cs`: 状态引用更新
  - 6个测试文件: 全部同步更新
- **文档修改项**:
  - `ruoyu.student/docs/overview/Design.md`: 枚举和方法名更新，新增锁定说明
  - `ruoyu.student/docs/database/tables/image_upload_record.md`: Status 字段和锁定说明
  - `ruoyu.student/docs/modules/ImageUpload/ImageUploadSubmit/01-FEATURE.md`: AC 和锁定说明
  - 22 个子模块文档: 全量替换旧引用
- **验证结果**: student 149 测试通过, admin 180 测试通过, user 74 测试通过

### ITEM-06: LegacyClean 标注定位变化

- **输入摘要**: LegacyClean 是运维兜底接口，正常流程不应遗留需要此接口处理的场景
- **代码修改项**:
  - `admin_portal/OssUploadRecordController.cs`: 添加 XML 文档注释
- **文档修改项**: 无额外文档需要更新（注释即文档）
- **验证结果**: 构建通过

## 5. 正式文档同步记录

| 文档路径 | 更新原因 | 更新摘要 |
|----------|----------|----------|
| ruoyu.mistake/docs/overview/Integration.md | ITEM-01/02/04 | 新增出向 gRPC 依赖，CompleteUploadReview 标记废弃 |
| ruoyu.mistake/docs/overview/Design.md | ITEM-02/04 | 新增外部依赖，CompleteUploadReview 标记废弃 |
| ruoyu.mistake/docs/modules/MistakeReview/ReviewMistakeItem/01-FEATURE.md | ITEM-01/02 | 补充编排描述和验收条件 |
| ruoyu.mistake/docs/modules/MistakeReview/ReviewMistakeItem/02-SPEC.md | ITEM-01/02 | 新增 FR-10/11/12 及验收标准 |
| ruoyu.mistake/docs/modules/MistakeReview/ReviewMistakeItem/03-DESIGN.md | ITEM-01/02/03 | 完整重写数据流和设计决策 |
| ruoyu.mistake/docs/modules/MistakeReview/CompleteUploadReview/01-FEATURE.md | ITEM-04 | 新增废弃声明 |
| ruoyu.mistake/docs/modules/MistakeReview/CompleteUploadReview/02-SPEC.md | ITEM-04 | 新增废弃声明 |
| ruoyu.mistake/docs/development/Troubleshooting.md | ITEM-02/04 | 新增第9节审核后排查，废弃说明 |
| ruoyu.student/docs/overview/Design.md | ITEM-05 | 枚举和方法名更新，锁定说明 |
| ruoyu.student/docs/database/tables/image_upload_record.md | ITEM-05 | Status 字段和锁定说明 |
| ruoyu.student/docs/modules/ImageUpload/ImageUploadSubmit/01-FEATURE.md | ITEM-05 | AC 和锁定说明 |
| ruoyu.student 22个子模块文档 | ITEM-05 | 全量替换旧引用 |

## 6. 未完成或暂时跳过的输入项

无。

## 7. 风险与待人工复核事项

- **部署注意**: Mistake 服务新增了对 Student.Contract 的项目引用和 gRPC 客户端依赖，部署时需确保 Student 服务可用
- **数据兼容**: UploadStatus 枚举值 3 的语义从"已完成"变为"审核中"，数据库中已有的 status=3 记录含义已变，需确认前端展示逻辑是否需要适配
- **CompletedRetentionDays 配置键**: 保留旧命名以兼容已有配置文件，但语义已对应 UnderReview 状态
- **CompleteUploadReview**: 标记 Obsolete 但未删除，LegacyClean 仍依赖它，后续版本可考虑移除

## 8. 流程反思

本轮任务违反了"先改文档再改代码"的核心原则。根因分析：
- 将 TriElementLifecycleAnalysis.md（分析文档）当作了"文档已足够"的借口，跳过了正式 docs 目录的更新
- AGENTS.md 规则本身没有问题，是执行纪律问题
- 后续任务必须严格遵守：文档修改完成并校验后，才能进入代码修改阶段
