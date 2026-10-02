# 错题本 gRPC 服务 — 需求摘要

## 服务级需求

- 提供错题记录的全生命周期 gRPC 服务（创建、审核、管理、练习、分析）
- 支持从 student 服务自动分发创建错题（SubmitMistakeUpload）
- 支持教师逐条审核 + 整批完成审核流程，审核时可修正科目/年级
- 审核完成后自动迁移 OSS 图片（uploads/ → mistakes/）
- 支持错题练习记录追踪，仅已确认错题可添加练习
- 支持错题处理过程日志
- 支持 OSS 垃圾清理审计（GetAllReferencedImagePaths）
- 数据库：PostgreSQL（Npgsql provider）
- 双 OSS 模式：S3OssService（生产）/ LocalFileOssService（开发，USE_LOCAL_OSS=1）
- 审核完成操作涉及跨服务副作用，单点失败不阻塞整体流程

## 功能需求索引

| 功能域 | 需求 | 详细文档 |
|--------|------|----------|
| MistakeDispatch | 提交错题分发 | [modules/MistakeDispatch/SubmitMistakeUpload/](../modules/SourceIntake.md) |
| MistakeReview | 审核错题条目 | [modules/MistakeReview/ReviewMistakeItem/](../modules/MistakeReview/ReviewMistakeItem/01-FEATURE.md) |
| MistakeReview | 查询待审核上传记录 | [modules/MistakeReview/GetPendingReviewUploads/](../modules/MistakeReview/GetPendingReviewUploads/01-FEATURE.md) |
| MistakeReview | 完成整批审核 | [modules/MistakeReview/CompleteUploadReview/](../modules/MistakeReview/CompleteUploadReview/01-FEATURE.md) |
| MistakeManagement | 查询错题条目 | [modules/MistakeManagement/GetMistakeItem/](../modules/MistakeManagement/GetMistakeItem/01-FEATURE.md) |
| MistakeManagement | 按上传记录查询错题 | [modules/MistakeManagement/GetMistakeItemsByUpload/](../modules/MistakeManagement/GetMistakeItemsByUpload/01-FEATURE.md) |
| MistakeManagement | 分页查询错题列表 | [modules/MistakeManagement/GetMistakeItemList/](../modules/MistakeManagement/GetMistakeItemList/01-FEATURE.md) |
| MistakeManagement | 手动添加错题条目 | [modules/MistakeManagement/AddMistakeItem/](../modules/SourceIntake.md) |
| MistakeManagement | 更新错题条目 | [modules/MistakeManagement/UpdateMistakeItem/](../modules/MistakeManagement/UpdateMistakeItem/01-FEATURE.md) |
| MistakeManagement | 删除错题条目 | [modules/MistakeManagement/DeleteMistakeItem/](../modules/MistakeManagement/DeleteMistakeItem/01-FEATURE.md) |
| MistakePractice | 添加练习记录 | [modules/MistakePractice/AddMistakePractice/](../modules/MistakePractice/AddMistakePractice/01-FEATURE.md) |
| MistakePractice | 查询练习记录 | [modules/MistakePractice/GetMistakePractices/](../modules/MistakePractice/GetMistakePractices/01-FEATURE.md) |
| MistakeProcessLogging | 记录处理日志 | [modules/MistakeProcessLogging/LogMistakeProcess/](../modules/MistakeProcessLogging/LogMistakeProcess/01-FEATURE.md) |
| MistakeProcessLogging | 查询处理历史 | [modules/MistakeProcessLogging/GetMistakeProcessHistory/](../modules/MistakeProcessLogging/GetMistakeProcessHistory/01-FEATURE.md) |
| ImagePathQuery | 获取所有引用图片路径 | [modules/ImagePathQuery/GetAllReferencedImagePaths/](../modules/ImagePathQuery/GetAllReferencedImagePaths/01-FEATURE.md) |

## 外部集成需求

| 集成点 | 详细文档 |
|--------|----------|
| OSS (MinIO/S3) | [docs/Integration/Oss/](../Integration/Oss/) |
| Student 服务 | [Student HTTP 集成](../Integration/Student.md) |
| Storage 服务 | [PostgreSQL 存储集成](../Integration/Storage.md) |
| Portals (前端门户) | [docs/Integration/Portals/](../Integration/Portals/) |
