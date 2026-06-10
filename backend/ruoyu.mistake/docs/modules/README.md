# 模块索引

ruoyu.mistake 分为以下业务域：

## 1. MistakeDispatch — 错题提交

| 功能名 | 功能ID | 说明 |
|--------|--------|------|
| [SubmitMistakeUpload](./MistakeDispatch/SubmitMistakeUpload/01-FEATURE.md) | `MistakeDispatch/SubmitMistakeUpload` | 提交一张图片中的多个错题到待审核队列 |

## 2. MistakeReview — 错题审核

| 功能名 | 功能ID | 说明 |
|--------|--------|------|
| [ReviewMistakeItem](./MistakeReview/ReviewMistakeItem/01-FEATURE.md) | `MistakeReview/ReviewMistakeItem` | 单个错题确认/驳回（含图片迁移） |
| [GetPendingReviewUploads](./MistakeReview/GetPendingReviewUploads/01-FEATURE.md) | `MistakeReview/GetPendingReviewUploads` | 分页查询待审核上传汇总 |
| [CompleteUploadReview](./MistakeReview/CompleteUploadReview/01-FEATURE.md) | `MistakeReview/CompleteUploadReview` | 整个上传完成审核后批量迁移图片 |

## 3. MistakeManagement — 错题管理

| 功能名 | 功能ID | 说明 |
|--------|--------|------|
| [GetMistakeItem](./MistakeManagement/GetMistakeItem/01-FEATURE.md) | `MistakeManagement/GetMistakeItem` | 根据ID查询单个错题 |
| [GetMistakeItemsByUpload](./MistakeManagement/GetMistakeItemsByUpload/01-FEATURE.md) | `MistakeManagement/GetMistakeItemsByUpload` | 根据上传记录ID查询错题列表 |
| [GetMistakeItemList](./MistakeManagement/GetMistakeItemList/01-FEATURE.md) | `MistakeManagement/GetMistakeItemList` | 分页查询错题列表，支持筛选 |
| [AddMistakeItem](./MistakeManagement/AddMistakeItem/01-FEATURE.md) | `MistakeManagement/AddMistakeItem` | 手动新增错题 |
| [UpdateMistakeItem](./MistakeManagement/UpdateMistakeItem/01-FEATURE.md) | `MistakeManagement/UpdateMistakeItem` | 更新错题元数据 |
| [DeleteMistakeItem](./MistakeManagement/DeleteMistakeItem/01-FEATURE.md) | `MistakeManagement/DeleteMistakeItem` | 删除错题（级联删除练习和日志） |

## 4. MistakePractice — 错题练习

| 功能名 | 功能ID | 说明 |
|--------|--------|------|
| [AddMistakePractice](./MistakePractice/AddMistakePractice/01-FEATURE.md) | `MistakePractice/AddMistakePractice` | 添加练习记录（仅已确认错题） |
| [GetMistakePractices](./MistakePractice/GetMistakePractices/01-FEATURE.md) | `MistakePractice/GetMistakePractices` | 查询错题练习历史 |

## 5. MistakeProcessLogging — 处理日志

| 功能名 | 功能ID | 说明 |
|--------|--------|------|
| [LogMistakeProcess](./MistakeProcessLogging/LogMistakeProcess/01-FEATURE.md) | `MistakeProcessLogging/LogMistakeProcess` | 记录处理动作 |
| [GetMistakeProcessHistory](./MistakeProcessLogging/GetMistakeProcessHistory/01-FEATURE.md) | `MistakeProcessLogging/GetMistakeProcessHistory` | 查询处理历史 |

## 6. MistakeAnalysis — 错题分析

| 功能名 | 功能ID | 说明 |
|--------|--------|------|
| [AnalyzeMistakeType](./MistakeAnalysis/AnalyzeMistakeType/01-FEATURE.md) | `MistakeAnalysis/AnalyzeMistakeType` | 分析错题类型（当前为占位实现） |

## 7. ImagePathQuery — 图片路径查询

| 功能名 | 功能ID | 说明 |
|--------|--------|------|
| [GetAllReferencedImagePaths](./ImagePathQuery/GetAllReferencedImagePaths/01-FEATURE.md) | `ImagePathQuery/GetAllReferencedImagePaths` | 查询所有引用的图片路径（用于清理） |
