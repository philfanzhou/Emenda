# 模块索引

ruoyu.mistake 分为以下业务域：

## 1. MistakeDispatch — 错题提交

| 功能名 | 功能ID | 说明 | 状态 |
|--------|--------|------|------|
| SubmitMistakeUpload | `MistakeDispatch/SubmitMistakeUpload` | 提交一张图片中的多个错题到待审核队列 | 待创建 |

## 2. MistakeReview — 错题审核

| 功能名 | 功能ID | 说明 | 状态 |
|--------|--------|------|------|
| ReviewMistakeItem | `MistakeReview/ReviewMistakeItem` | 单个错题确认/驳回 | 待创建 |
| GetPendingReviewUploads | `MistakeReview/GetPendingReviewUploads` | 分页查询待审核上传汇总 | 待创建 |
| CompleteUploadReview | `MistakeReview/CompleteUploadReview` | 整个上传完成审核后迁移图片 | 待创建 |

## 3. MistakeManagement — 错题管理

| 功能名 | 功能ID | 说明 | 状态 |
|--------|--------|------|------|
| [GetMistakeItemList](./MistakeManagement/GetMistakeItemList/01-FEATURE.md) | `MistakeManagement/GetMistakeItemList` | 分页查询错题列表，支持筛选 | 已完成 |
| AddMistakeItem | `MistakeManagement/AddMistakeItem` | 手动新增错题 | 待创建 |
| GetMistakeItem | `MistakeManagement/GetMistakeItem` | 根据ID查询单个错题 | 待创建 |
| UpdateMistakeItem | `MistakeManagement/UpdateMistakeItem` | 更新错题元数据 | 待创建 |
| DeleteMistakeItem | `MistakeManagement/DeleteMistakeItem` | 删除错题（级联删除） | 待创建 |

## 4. MistakePractice — 错题练习

| 功能名 | 功能ID | 说明 | 状态 |
|--------|--------|------|------|
| AddMistakePractice | `MistakePractice/AddMistakePractice` | 添加练习记录 | 待创建 |
| GetMistakePractices | `MistakePractice/GetMistakePractices` | 查询错题练习历史 | 待创建 |

## 5. MistakeProcessLogging — 处理日志

| 功能名 | 功能ID | 说明 | 状态 |
|--------|--------|------|------|
| LogMistakeProcess | `MistakeProcessLogging/LogMistakeProcess` | 记录处理动作 | 待创建 |
| GetMistakeProcessHistory | `MistakeProcessLogging/GetMistakeProcessHistory` | 查询处理历史 | 待创建 |

## 6. MistakeAnalysis — 错题分析

| 功能名 | 功能ID | 说明 | 状态 |
|--------|--------|------|------|
| AnalyzeMistakeType | `MistakeAnalysis/AnalyzeMistakeType` | 分析错题类型 | 待创建 |

## 7. ImagePathQuery — 图片路径查询

| 功能名 | 功能ID | 说明 | 状态 |
|--------|--------|------|------|
| GetAllReferencedImagePaths | `ImagePathQuery/GetAllReferencedImagePaths` | 查询所有引用的图片路径（用于清理） | 待创建 |