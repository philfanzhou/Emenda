# Student 与 Mistake 双向 gRPC 集成

## 功能名称和一句话概括

Student-Mistake 双向集成 — `ruoyu.student` 与 `ruoyu.mistake` 通过 gRPC 实现错题创建与图片清理的双向协作。

## 所属功能块

- 功能块：`Integration / Student`
- 方向：`student <-> mistake`（双向）

## 核心用户故事

1. **自动创建错题**：作为上传分析链路，当 VL 分析将图片分组后，student 服务自动调用 mistake 的 `SubmitMistakeUpload` 创建错题条目。
2. **审核后清理图片**：作为审核完成流程，mistake 服务在 `CompleteUploadReview` 完成后，自动调用 student 的 `RemoveImagesFromRecord` 清理已处理的图片。
3. **服务降级容错**：作为系统运维人员，我希望 student 服务临时不可用时，mistake 的审核完成流程不会因此回滚。

## 功能边界

### 方向1: student → mistake (SubmitMistakeUpload)

- **触发条件**：`UploadAnalysisWorker` 完成 VL 分析，且分析结果包含分组（`HasGroups`）
- **调用方**：`GrpcMistakeSubmitter`（实现 `IMistakeSubmitter`）
- **分发方**：`GrpcUploadDispatcher`（实现 `IUploadDispatcher`），提供 `DispatchToMistakeAsync` 和 `IsMistakeServiceAvailableAsync`
- **行为**：遍历每个 group，独立调用 `SubmitMistakeUpload`；单个 group 失败不影响其他 group
- **超时**：30 秒

### 方向2: mistake → student (RemoveImagesFromRecord)

- **触发条件**：`CompleteUploadReview` 执行完成，存在需要清理的图片路径，且 `studentId` 非空
- **调用方**：`MistakeServiceImpl` 直接使用 `StudentLearningGrpcServiceClient`
- **行为**：构建 `RemoveImagesFromRecordRequest`，调用 `RemoveImagesFromRecordAsync`
- **失败策略**：仅 log error，不回滚审核完成流程

## 核心约束

| 约束 | 说明 |
|------|------|
| SubmitMistakeUpload 非幂等 | 每次调用创建新错题条目，`source_upload_id` 不做唯一性校验 |
| 审核完成不回滚 | `CompleteUploadReview` 中 student 回调失败不回滚整个审核完成流程 |
| 单 group 隔离 | UploadAnalysisWorker 中每个 group 独立提交，互不影响 |
| 图片清理尽力而为 | `RemoveImagesFromRecord` 失败仅记录日志，不影响审核结果 |

## 范围外

- 不实现 Mistake 服务端创建错题的业务逻辑
- 不实现 Student 服务端图片清理的业务逻辑
- 不实现跨服务分布式事务
- 不实现 portal 页面交互
- 不实现重试队列或补偿机制

## 文档索引

- [02-SPEC.md](./02-SPEC.md)
- [03-DESIGN.md](./03-DESIGN.md)
- [04-TASKS.md](./04-TASKS.md)
- [05-TESTS.md](./05-TESTS.md)
- [06-CONVENTIONS.md](./06-CONVENTIONS.md)
