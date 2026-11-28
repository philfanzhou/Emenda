# 功能索引

错题只通过 AI Tutor 自动提取（[AutomaticExtraction](./AutomaticExtraction.md)）或 AI Tutor 对话采集（[Conversation collection](../Integration/ConversationCollection.md)）进入 Emenda；以下是入库之后的生命周期功能。

| 业务域 | 功能点入口 |
|--------|------------|
| Intake | [AutomaticExtraction](./AutomaticExtraction.md) |
| ImagePathQuery | [GetAllReferencedImagePaths](./ImagePathQuery/GetAllReferencedImagePaths/01-FEATURE.md) |
| MistakeManagement | [DeleteMistakeItem](./MistakeManagement/DeleteMistakeItem/01-FEATURE.md), [GetMistakeItem](./MistakeManagement/GetMistakeItem/01-FEATURE.md), [GetMistakeItemList](./MistakeManagement/GetMistakeItemList/01-FEATURE.md), [GetMistakeItemsByUpload](./MistakeManagement/GetMistakeItemsByUpload/01-FEATURE.md), [UpdateMistakeItem](./MistakeManagement/UpdateMistakeItem/01-FEATURE.md) |
| MistakeProcessLogging | [GetMistakeProcessHistory](./MistakeProcessLogging/GetMistakeProcessHistory/01-FEATURE.md), [LogMistakeProcess](./MistakeProcessLogging/LogMistakeProcess/01-FEATURE.md) |
| MistakeReview | [ReviewMistakeItem](./MistakeReview/ReviewMistakeItem.md) |

旧的上传入口（`/api/mistakes/upload`、`POST /api/mistakes`、`/api/intake/uploads`）、UploadPollingWorker、VL 分组与结构化重分析、CompleteUploadReview 以及受管来源入库/目标/退回/交接/对账/版面作业均已移除（schema v19）。
