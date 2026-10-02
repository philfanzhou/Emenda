# 功能索引

| 业务域 | 功能点入口 |
|--------|------------|
| ImagePathQuery | [GetAllReferencedImagePaths](./ImagePathQuery/GetAllReferencedImagePaths/01-FEATURE.md) |
| MistakeDispatch | [SubmitMistakeUpload](./SourceIntake.md) — 由 UploadPollingWorker 内部调用 |
| MistakeManagement | [AddMistakeItem](./SourceIntake.md), [DeleteMistakeItem](./MistakeManagement/DeleteMistakeItem/01-FEATURE.md), [GetMistakeItem](./MistakeManagement/GetMistakeItem/01-FEATURE.md), [GetMistakeItemList](./MistakeManagement/GetMistakeItemList/01-FEATURE.md), [GetMistakeItemsByUpload](./MistakeManagement/GetMistakeItemsByUpload/01-FEATURE.md), [UpdateMistakeItem](./MistakeManagement/UpdateMistakeItem/01-FEATURE.md) |
| MistakePractice | [AddMistakePractice](./MistakePractice/AddMistakePractice/01-FEATURE.md), [GetMistakePractices](./MistakePractice/GetMistakePractices/01-FEATURE.md) |
| MistakeProcessLogging | [GetMistakeProcessHistory](./MistakeProcessLogging/GetMistakeProcessHistory/01-FEATURE.md), [LogMistakeProcess](./MistakeProcessLogging/LogMistakeProcess/01-FEATURE.md) |
| MistakeReview | ~~[CompleteUploadReview]~~ (已废弃), [GetPendingReviewUploads](./MistakeReview/GetPendingReviewUploads/01-FEATURE.md), [ReviewMistakeItem](./MistakeReview/ReviewMistakeItem.md), [ReanalyzeMistakeItem](./MistakeReview/ReanalyzeMistakeItem/README.md) |
| UploadPolling | UploadPollingWorker — 轮询 Student 的 Processing 记录，VL 分析，创建错题，OSS 迁移 |
| VlImageAnalysis | VlUploadAnalyzer — OpenAI 兼容 VL 视觉语言图片分析，按学科/年级分组 |

- [不可变来源幂等入库](./SourceIntake.md)

- [SourceTargets](./SourceTargets.md) — 保留来源、持久候选与核验目标恢复性物化。

- [受管持久退回](./SourceReturns.md)
