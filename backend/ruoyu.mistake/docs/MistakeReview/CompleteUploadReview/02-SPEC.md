# CompleteUploadReview — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：完成上传审核是审核流程的最后一步，在上传记录中所有错题均已审核完毕后，执行 OSS 图片迁移（已确认错题从临时上传目录迁移到正式错题目录）和上传记录清理（调用 Student 服务移除图片引用），确保数据与存储的一致性。

**用户故事 1（老师完成审核批次）**：作为老师，当我对某个上传记录中的所有错题完成逐条审核后，点击"完成审核"触发图片迁移和清理；系统检查无待审核错题后，将已确认的图片归档到正式目录，清理上传记录。

## 功能要求清单（可独立测试）

- [x] FR-01 通过 gRPC 接收 `CompleteUploadReviewRequest`（source_upload_id / reviewer_id），返回 `CompleteUploadReviewResponse`（success / error_message / student_id）。
- [x] FR-02 前置校验：上传记录中不得有 PendingReview 状态的错题，否则返回 `Success = false` 及错误消息。
- [x] FR-03 前置校验：上传记录中至少有一条错题，否则返回 `Success = false` 及错误消息。
- [x] FR-04 对已确认（Confirmed）错题：将 `source_regions` 中 `uploads/` 前缀的图片路径拷贝到 `mistakes/` 路径，删除原 `uploads/` 路径文件，更新 `source_regions` 中的路径为新路径。
- [x] FR-05 对已驳回（Rejected）错题：跳过 OSS 图片迁移。
- [x] FR-06 路径映射缓存：同一旧路径多次引用时，仅迁移一次，后续直接复用缓存的新路径。
- [x] FR-07 非 `uploads/` 前缀的路径跳过迁移，不修改也不报错。
- [x] FR-08 单个图片迁移失败时记录 Error 日志，不中断整个流程。
- [x] FR-09 路径更新后调用 `_mistakeService.UpdateMistakeItemAsync` 持久化更新的 `source_regions`。
- [x] FR-10 收集所有错题的图片路径（含确认和驳回），调用 Student 服务 `RemoveImagesFromRecord` 清理上传记录中的图片引用。
- [x] FR-11 `RemoveImagesFromRecord` 调用失败时记录 Error 日志，不影响整体 `Success` 返回。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用 `CompleteUploadReview` gRPC 方法传入有效的 `source_upload_id`（所有错题已审核完毕），返回 `CompleteUploadReviewResponse { Success = true, StudentId = "xxx" }`。
- AC-FR-02：传入的 `source_upload_id` 下存在 PendingReview 状态的错题，返回 `Success = false`，`ErrorMessage` 为 "还有 N 条错题待审核，请先完成所有审核"。
- AC-FR-03：传入不存在的 `source_upload_id`，返回 `Success = false`，`ErrorMessage` 为 "未找到该上传记录的错题"。
- AC-FR-04：已确认错题的图片路径从 `uploads/xxx.png` 变为 `mistakes/xxx.png`，OSS 中原路径文件被删除，新路径文件存在。
- AC-FR-05：已驳回错题的 `source_regions` 路径不变，OSS 中不执行拷贝/删除操作。
- AC-FR-06：两条错题引用相同图片 `uploads/shared.png` 时，OSS 仅执行一次 copy/delete，两条路径均被更新。
- AC-FR-07：路径不以 `uploads/` 开头（如 `other/xxx.png`）时，`source_regions` 路径保持不变。
- AC-FR-08：OSS CopyObjectAsync 抛异常时，记录 `_logger.LogError`，后续图片继续处理，最终 `Success = true`。
- AC-FR-09：路径更新后，数据库 `source_regions` 中对应路径已更新。
- AC-FR-10：Student 服务 `RemoveImagesFromRecord` 被调用一次，`RecordId` 与 `source_upload_id` 一致，`ImagePaths` 包含所有错题的去重图片路径。
- AC-FR-11：Student 服务调用失败时，记录 Error 日志，返回 `Success = true`（Student 服务调用不影响主流程）。

## 非功能需求

- **性能**：完成审核操作 P95 < 5s（取决于 OSS 操作耗时和错题数量）。
- **安全**：仅审核人员可执行完成审核（由上层 API 网关负责鉴权）。
- **可靠性**：单张图片迁移失败不阻塞整体流程；OSS 操作部分失败时已成功迁移的图片不回滚。
- **可观测性**：每次 OSS 图片迁移记录 Information 日志（含 oldPath/newPath）；迁移失败记录 Error 日志（含异常信息和路径）。

## 测试策略

- **覆盖率目标**：`MistakeServiceImpl.CompleteUploadReview` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：存在待审核错题、上传记录不存在、OSS 拷贝失败、Student 服务调用失败、路径映射缓存、非 uploads/ 前缀路径。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（OSS 服务、Student gRPC 客户端、仓储）均可替换。
- **禁止事项**：不得在测试中访问真实 OSS；不得调用真实 Student 服务。