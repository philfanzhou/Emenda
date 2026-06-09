# ruoyu.mistake 集成接口清单

本文档列出 `ruoyu.mistake` 服务对外暴露的全部 gRPC 接口，包括调用方向、参数定义、错误语义、幂等性及跨服务副作用。

---

## 目录

- [1. 接口总览](#1-接口总览)
- [2. 接口详细定义](#2-接口详细定义)
  - [2.1 SubmitMistakeUpload](#21-submitmistakeupload)
  - [2.2 GetMistakeItem](#22-getmistakeitem)
  - [2.3 GetMistakeItemsByUpload](#23-getmistakeitemsbyupload)
  - [2.4 GetMistakeItemList](#24-getmistakeitemlist)
  - [2.5 GetPendingReviewUploads](#25-getpendingreviewuploads)
  - [2.6 ReviewMistakeItem](#26-reviewmistakeitem)
  - [2.7 AddMistakeItem](#27-addmistakeitem)
  - [2.8 UpdateMistakeItem](#28-updatemistakeitem)
  - [2.9 DeleteMistakeItem](#29-deletemistakeitem)
  - [2.10 CompleteUploadReview](#210-completeuploadreview)
  - [2.11 AddMistakePractice](#211-addmistakepractice)
  - [2.12 GetMistakePractices](#212-getmistakepractices)
  - [2.13 AnalyzeMistakeType](#213-analyzemistaketype)
  - [2.14 LogMistakeProcess](#214-logmistakeprocess)
  - [2.15 GetMistakeProcessHistory](#215-getmistakeprocesshistory)
  - [2.16 GetAllReferencedImagePaths](#216-getallreferencedimagepaths)
- [3. 枚举定义](#3-枚举定义)
- [4. 公共消息类型](#4-公共消息类型)
- [5. 跨服务副作用汇总](#5-跨服务副作用汇总)
- [6. 幂等性与兼容性注意事项](#6-幂等性与兼容性注意事项)

---

## 1. 接口总览

| # | 接口名称 | 协议 | 调用方 | 功能说明 |
|---|---------|------|--------|---------|
| 1 | SubmitMistakeUpload | gRPC | student | 学生提交错题上传，创建错题条目 |
| 2 | GetMistakeItem | gRPC | admin_portal, teacher_portal, user_portal | 按 ID 获取单条错题详情 |
| 3 | GetMistakeItemsByUpload | gRPC | admin_portal, teacher_portal | 按上传批次获取错题列表 |
| 4 | GetMistakeItemList | gRPC | admin_portal, teacher_portal, user_portal | 分页查询错题列表（支持筛选） |
| 5 | GetPendingReviewUploads | gRPC | teacher_portal | 获取待审核的上传批次列表 |
| 6 | ReviewMistakeItem | gRPC | teacher_portal | 审核单条错题（确认/驳回） |
| 7 | AddMistakeItem | gRPC | teacher_portal | 手动添加错题条目 |
| 8 | UpdateMistakeItem | gRPC | admin_portal | 更新错题条目信息 |
| 9 | DeleteMistakeItem | gRPC | teacher_portal, user_portal | 删除错题条目 |
| 10 | CompleteUploadReview | gRPC | teacher_portal | 完成整批上传的审核 |
| 11 | AddMistakePractice | gRPC | — | 添加错题练习记录 |
| 12 | GetMistakePractices | gRPC | — | 获取错题的练习记录列表 |
| 13 | AnalyzeMistakeType | gRPC | — | 分析错题类型 |
| 14 | LogMistakeProcess | gRPC | — | 记录错题处理过程日志 |
| 15 | GetMistakeProcessHistory | gRPC | — | 获取错题处理过程历史 |
| 16 | GetAllReferencedImagePaths | gRPC | admin_portal | 获取所有被引用的图片路径（用于 OSS 审计） |

---

## 2. 接口详细定义

### 2.1 SubmitMistakeUpload

| 属性 | 值 |
|------|-----|
| 接口名称 | SubmitMistakeUpload |
| 协议类型 | gRPC |
| 调用方向 | student → mistake |
| 调用方 | GrpcMistakeSubmitter (IMistakeSubmitter), GrpcUploadDispatcher.DispatchToMistakeAsync |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 学生提交错题上传，根据图片和元数据创建错题条目。每次调用创建新条目，非幂等。 |
| 跨服务副作用 | 记录 ProcessLog (CREATED) |

**请求参数 — SubmitMistakeUploadRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| student_id | 必填 | string | 非空 | 学生 ID |
| subject | 必填 | int32 | 1–9 | 科目编号 |
| grade | 必填 | int32 | 1–12 | 年级编号 |
| image_paths | 必填 | repeated string | 至少 1 项 | 上传的图片路径列表 |
| comments | 可选 | string | — | 备注说明 |
| source_upload_id | 必填 | string | 非空 | 来源上传批次 ID |

**响应参数 — SubmitMistakeUploadResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| success | 必填 | bool | — | 操作是否成功 |
| error_message | 必填 | string | — | 失败时的错误信息 |
| created_item_ids | 必填 | repeated string | — | 创建的错题条目 ID 列表 |

**错误语义**

| 场景 | 说明 |
|------|------|
| student_id 为空 | 返回 success=false，error_message 描述参数缺失 |
| subject/grade 超出范围 | 返回 success=false，error_message 描述参数越界 |
| image_paths 为空 | 返回 success=false，error_message 描述至少需要 1 张图片 |
| source_upload_id 为空 | 返回 success=false，error_message 描述参数缺失 |

**幂等性**：非幂等，每次调用创建新条目。

---

### 2.2 GetMistakeItem

| 属性 | 值 |
|------|-----|
| 接口名称 | GetMistakeItem |
| 协议类型 | gRPC |
| 调用方向 | admin_portal / teacher_portal / user_portal → mistake |
| 调用方 | admin_portal: MistakeController.GetMistakeItem; teacher_portal: MistakeController.GetMistake, BatchReview, ReviewItem; user_portal: MistakeController.GetMistake |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 按错题 ID 获取单条错题详情 |

**请求参数 — IdRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| id | 必填 | string | 非空 | 错题条目 ID |

**响应参数 — MistakeItemDto**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| id | 必填 | string | — | 错题条目 ID |
| student_id | 必填 | string | — | 所属学生 ID |
| subject | 必填 | int32 | — | 科目编号 |
| grade | 必填 | int32 | — | 年级编号 |
| source_upload_id | 必填 | string | — | 来源上传批次 ID |
| source_regions | 必填 | repeated SourceRegion | — | 图片裁剪区域信息 |
| review_status | 必填 | ReviewStatus | — | 审核状态 |
| reviewer_id | 可选 | string | — | 审核人 ID |
| reviewed_at | 可选 | string | — | 审核时间 |
| review_comment | 可选 | string | — | 审核备注 |
| type | 可选 | int32 | — | 错题类型 |
| question_id | 可选 | string | — | 关联题目 ID |
| created_at | 必填 | string | — | 创建时间 |
| updated_at | 必填 | string | — | 更新时间 |

**错误语义**

| 场景 | 说明 |
|------|------|
| id 不存在 | gRPC NOT_FOUND |

**幂等性**：幂等。

---

### 2.3 GetMistakeItemsByUpload

| 属性 | 值 |
|------|-----|
| 接口名称 | GetMistakeItemsByUpload |
| 协议类型 | gRPC |
| 调用方向 | admin_portal / teacher_portal → mistake |
| 调用方 | admin_portal: MistakeController.GetMistakesByUploadId; teacher_portal: MistakeController.ReviewDetail, PendingItems |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 按上传批次 ID 获取该批次下的全部错题列表 |

**请求参数 — GetMistakeItemsByUploadRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| source_upload_id | 必填 | string | 非空 | 上传批次 ID |

**响应参数 — MistakeItemListResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| items | 必填 | repeated MistakeItemDto | — | 错题条目列表 |

**错误语义**

| 场景 | 说明 |
|------|------|
| source_upload_id 为空 | gRPC INVALID_ARGUMENT |
| source_upload_id 不存在 | 返回空列表 |

**幂等性**：幂等。

---

### 2.4 GetMistakeItemList

| 属性 | 值 |
|------|-----|
| 接口名称 | GetMistakeItemList |
| 协议类型 | gRPC |
| 调用方向 | admin_portal / teacher_portal / user_portal → mistake |
| 调用方 | admin_portal: MistakeController.GetMistakeItems (GET /api/admin/mistakes); teacher_portal: MistakeController.MistakeList; user_portal: MistakeController.MistakeList (GET /api/mistake/list) |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 分页查询错题列表，支持按学生、科目、年级、审核状态筛选 |

**请求参数 — GetMistakeItemListRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| student_id | 可选 | string | — | 按学生 ID 筛选 |
| subject | 可选 | int32 | 1–9 | 按科目筛选 |
| grade | 可选 | int32 | 1–12 | 按年级筛选 |
| review_status | 可选 | ReviewStatus | — | 按审核状态筛选 |
| page | 必填 | int32 | ≥ 1 | 页码 |
| size | 必填 | int32 | ≥ 1 | 每页条数 |

**响应参数 — MistakeItemPageResult**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| items | 必填 | repeated MistakeItemDto | — | 当前页错题条目列表 |
| page_meta | 必填 | PageMeta | — | 分页元数据 |

**错误语义**

| 场景 | 说明 |
|------|------|
| page/size 缺失或 ≤ 0 | gRPC INVALID_ARGUMENT |

**幂等性**：幂等（只读查询）。

---

### 2.5 GetPendingReviewUploads

| 属性 | 值 |
|------|-----|
| 接口名称 | GetPendingReviewUploads |
| 协议类型 | gRPC |
| 调用方向 | teacher_portal → mistake |
| 调用方 | MistakeController.PendingReviews, PendingItems |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 获取待审核的上传批次列表，支持按科目筛选 |

**请求参数 — GetPendingReviewUploadsRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| page | 必填 | int32 | ≥ 1 | 页码 |
| size | 必填 | int32 | ≥ 1 | 每页条数 |
| subjects | 可选 | repeated int32 | 1–9 | 按科目列表筛选 |

**响应参数 — PendingReviewUploadListResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| uploads | 必填 | repeated PendingReviewUploadDto | — | 待审核上传批次列表 |
| page_meta | 必填 | PageMeta | — | 分页元数据 |

**错误语义**

| 场景 | 说明 |
|------|------|
| page/size 缺失或 ≤ 0 | gRPC INVALID_ARGUMENT |

**幂等性**：幂等（只读查询）。

---

### 2.6 ReviewMistakeItem

| 属性 | 值 |
|------|-----|
| 接口名称 | ReviewMistakeItem |
| 协议类型 | gRPC |
| 调用方向 | teacher_portal → mistake |
| 调用方 | MistakeController.ReviewItem, BatchReview |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 教师审核单条错题，确认或驳回。仅可审核 PendingReview 状态的条目。 |
| 跨服务副作用 | 记录 ProcessLog (REVIEW_CONFIRMED / REVIEW_REJECTED) |

**请求参数 — ReviewMistakeItemRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| id | 必填 | string | 非空 | 错题条目 ID |
| review_status | 必填 | ReviewStatus | CONFIRMED / REJECTED | 审核结果 |
| reviewer_id | 可选 | string | — | 审核人 ID |
| review_comment | 可选 | string | — | 审核备注 |
| grade | 可选 | int32 | 1–12 | 审核时可修正年级 |
| subject | 可选 | int32 | 1–9 | 审核时可修正科目 |

**响应参数 — BoolResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| success | 必填 | bool | — | 操作是否成功 |
| error_message | 可选 | string | — | 失败时的错误信息 |

**错误语义**

| 场景 | 说明 |
|------|------|
| id 不存在 | success=false, error_message 描述未找到 |
| review_status 非 CONFIRMED/REJECTED | success=false, error_message 描述无效审核状态 |
| 条目非 PendingReview 状态 | success=false, error_message 描述状态不允许审核 |

**幂等性**：非幂等但安全，只能审核 PendingReview 状态的条目，重复审核同一状态会被拒绝。

---

### 2.7 AddMistakeItem

| 属性 | 值 |
|------|-----|
| 接口名称 | AddMistakeItem |
| 协议类型 | gRPC |
| 调用方向 | teacher_portal → mistake |
| 调用方 | MistakeController.AddItem |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 教师手动添加错题条目（如审核时拆分或补充） |
| 跨服务副作用 | 记录 ProcessLog (CREATED) |

**请求参数 — AddMistakeItemRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| student_id | 必填 | string | 非空 | 学生 ID |
| subject | 必填 | int32 | 0–9 | 科目编号（0 表示未指定） |
| grade | 必填 | int32 | 0–12 | 年级编号（0 表示未指定） |
| source_upload_id | 必填 | string | 非空 | 来源上传批次 ID |
| source_regions | 必填 | repeated SourceRegion | 至少 1 项 | 图片裁剪区域 |
| comments | 可选 | string | — | 备注说明 |

**响应参数 — MistakeItemDto**

参见 [2.2 GetMistakeItem](#22-getmistakeitem) 中的 MistakeItemDto 定义。

**错误语义**

| 场景 | 说明 |
|------|------|
| student_id 为空 | gRPC INVALID_ARGUMENT |
| source_upload_id 为空 | gRPC INVALID_ARGUMENT |
| source_regions 为空 | gRPC INVALID_ARGUMENT |

**幂等性**：非幂等，每次调用创建新条目。

---

### 2.8 UpdateMistakeItem

| 属性 | 值 |
|------|-----|
| 接口名称 | UpdateMistakeItem |
| 协议类型 | gRPC |
| 调用方向 | admin_portal → mistake |
| 调用方 | MistakeController.UpdateMistakeItem (PUT /api/admin/mistakes/{id}) |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 更新错题条目信息（科目、年级、裁剪区域等） |

**请求参数 — UpdateMistakeItemRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| id | 必填 | string | 非空 | 错题条目 ID |
| student_id | 可选 | string | — | 修改学生 ID |
| subject | 可选 | int32 | 1–9 | 修改科目 |
| grade | 可选 | int32 | 1–12 | 修改年级 |
| source_regions | 可选 | repeated SourceRegion | — | 修改图片裁剪区域 |

**响应参数 — MistakeItemDto**

参见 [2.2 GetMistakeItem](#22-getmistakeitem) 中的 MistakeItemDto 定义。

**错误语义**

| 场景 | 说明 |
|------|------|
| id 不存在 | gRPC NOT_FOUND |
| 无任何字段更新 | 返回当前条目数据 |

**幂等性**：非幂等，多次调用以最终一次更新为准。

---

### 2.9 DeleteMistakeItem

| 属性 | 值 |
|------|-----|
| 接口名称 | DeleteMistakeItem |
| 协议类型 | gRPC |
| 调用方向 | teacher_portal / user_portal → mistake |
| 调用方 | teacher_portal: MistakeController.ReturnMistake, BatchReview(驳回), ReviewItem(驳回); user_portal: MistakeController.DeleteMistake (DELETE /api/mistake/{id}) |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 删除错题条目 |

**请求参数 — IdRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| id | 必填 | string | 非空 | 错题条目 ID |

**响应参数 — BoolResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| success | 必填 | bool | — | 操作是否成功 |
| error_message | 可选 | string | — | 失败时的错误信息 |

**错误语义**

| 场景 | 说明 |
|------|------|
| id 不存在 | success=false |

**幂等性**：幂等，不存在时返回 false。

---

### 2.10 CompleteUploadReview

| 属性 | 值 |
|------|-----|
| 接口名称 | CompleteUploadReview |
| 协议类型 | gRPC |
| 调用方向 | teacher_portal → mistake |
| 调用方 | MistakeController.CompleteReview, ReviewItem(确认后自动), BatchReview(确认后自动) |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 完成整批上传的审核，触发 OSS 文件迁移和 student 服务回调 |
| 跨服务副作用 | 1) OSS copy/delete（uploads/ → mistakes/）；2) 回调 student.RemoveImagesFromRecord |

**请求参数 — CompleteUploadReviewRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| source_upload_id | 必填 | string | 非空 | 上传批次 ID |
| reviewer_id | 可选 | string | — | 审核人 ID |

**响应参数 — CompleteUploadReviewResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| success | 必填 | bool | — | 操作是否成功 |
| error_message | 必填 | string | — | 失败时的错误信息 |
| student_id | 必填 | string | — | 涉及的学生 ID（供调用方做后续处理） |

**错误语义**

| 场景 | 说明 |
|------|------|
| source_upload_id 为空 | gRPC INVALID_ARGUMENT |
| source_upload_id 不存在 | success=false, error_message 描述未找到 |
| OSS 操作失败 | success=false, error_message 描述 OSS 错误（注意：已执行的操作不可回滚） |
| student 回调失败 | success=false, error_message 描述回调失败 |

**幂等性**：非幂等，OSS 操作和路径更新不可回滚。重复调用可能导致异常，调用方需确保仅调用一次。

**兼容性注意事项**：此接口涉及跨服务副作用（OSS 文件迁移 + student 服务回调），调用方必须做好失败后的补偿处理。OSS copy/delete 不具备事务性，部分成功后无法自动回滚。

---

### 2.11 AddMistakePractice

| 属性 | 值 |
|------|-----|
| 接口名称 | AddMistakePractice |
| 协议类型 | gRPC |
| 调用方向 | — → mistake |
| 调用方 | 内部调用 |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 为错题添加练习记录 |
| 跨服务副作用 | 记录 ProcessLog (PRACTICED) |

**请求参数 — AddMistakePracticeRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| mistake_item_id | 必填 | string | 非空 | 错题条目 ID |
| student_id | 必填 | string | 非空 | 学生 ID |
| image_paths | 可选 | repeated string | — | 练习图片路径 |
| student_answer | 可选 | string | — | 学生答案 |
| is_correct | 必填 | bool | — | 是否正确 |
| comments | 可选 | string | — | 备注说明 |

**响应参数 — MistakePracticeDto**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| id | 必填 | string | — | 练习记录 ID |
| mistake_item_id | 必填 | string | — | 关联错题 ID |
| student_id | 必填 | string | — | 学生 ID |
| practice_date | 必填 | string | — | 练习日期 |
| image_paths | 必填 | repeated string | — | 练习图片路径 |
| student_answer | 可选 | string | — | 学生答案 |
| is_correct | 必填 | bool | — | 是否正确 |
| comments | 可选 | string | — | 备注说明 |

**错误语义**

| 场景 | 说明 |
|------|------|
| mistake_item_id 不存在 | gRPC INVALID_ARGUMENT |
| student_id 为空 | gRPC INVALID_ARGUMENT |

**幂等性**：非幂等，每次调用创建新练习记录。

---

### 2.12 GetMistakePractices

| 属性 | 值 |
|------|-----|
| 接口名称 | GetMistakePractices |
| 协议类型 | gRPC |
| 调用方向 | — → mistake |
| 调用方 | 内部调用 |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 获取指定错题的练习记录列表 |

**请求参数 — IdRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| id | 必填 | string | 非空 | 错题条目 ID |

**响应参数 — MistakePracticeListResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| practices | 必填 | repeated MistakePracticeDto | — | 练习记录列表 |

**错误语义**

| 场景 | 说明 |
|------|------|
| id 不存在 | 返回空列表 |

**幂等性**：幂等（只读查询）。

---

### 2.13 AnalyzeMistakeType

| 属性 | 值 |
|------|-----|
| 接口名称 | AnalyzeMistakeType |
| 协议类型 | gRPC |
| 调用方向 | — → mistake |
| 调用方 | 内部调用 |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 分析错题类型，返回类型编号、名称和分析依据 |
| 跨服务副作用 | 记录 ProcessLog (ANALYZED) |

**请求参数 — AnalyzeMistakeRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| mistake_item_id | 必填 | string | 非空 | 错题条目 ID |
| content | 可选 | string | — | 题目内容 |
| correct_answer | 可选 | string | — | 正确答案 |
| student_answer | 可选 | string | — | 学生答案 |

**响应参数 — MistakeTypeResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| type | 必填 | int32 | — | 错题类型编号 |
| type_name | 必填 | string | — | 错题类型名称 |
| analysis_reason | 必填 | string | — | 分析依据说明 |

**错误语义**

| 场景 | 说明 |
|------|------|
| mistake_item_id 不存在 | gRPC INVALID_ARGUMENT |
| 分析服务不可用 | gRPC UNAVAILABLE |

**幂等性**：非幂等，每次分析可能产生不同结果（取决于分析模型），且每次都会记录 ProcessLog。

---

### 2.14 LogMistakeProcess

| 属性 | 值 |
|------|-----|
| 接口名称 | LogMistakeProcess |
| 协议类型 | gRPC |
| 调用方向 | — → mistake |
| 调用方 | 内部调用 |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 记录错题处理过程日志 |

**请求参数 — LogMistakeProcessRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| mistake_item_id | 必填 | string | 非空 | 错题条目 ID |
| student_id | 可选 | string | — | 学生 ID |
| action | 必填 | ProcessAction | — | 处理动作类型 |
| details | 可选 | string | — | 详细说明 |

**响应参数 — BoolResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| success | 必填 | bool | — | 操作是否成功 |
| error_message | 可选 | string | — | 失败时的错误信息 |

**错误语义**

| 场景 | 说明 |
|------|------|
| mistake_item_id 为空 | gRPC INVALID_ARGUMENT |
| action 为 UNSPECIFIED | gRPC INVALID_ARGUMENT |

**幂等性**：非幂等，每次调用追加一条日志。

---

### 2.15 GetMistakeProcessHistory

| 属性 | 值 |
|------|-----|
| 接口名称 | GetMistakeProcessHistory |
| 协议类型 | gRPC |
| 调用方向 | — → mistake |
| 调用方 | 内部调用 |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 获取指定错题的处理过程历史日志 |

**请求参数 — IdRequest**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| id | 必填 | string | 非空 | 错题条目 ID |

**响应参数 — ProcessLogListResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| logs | 必填 | repeated ProcessLogDto | — | 处理日志列表 |

**错误语义**

| 场景 | 说明 |
|------|------|
| id 不存在 | 返回空列表 |

**幂等性**：幂等（只读查询）。

---

### 2.16 GetAllReferencedImagePaths

| 属性 | 值 |
|------|-----|
| 接口名称 | GetAllReferencedImagePaths |
| 协议类型 | gRPC |
| 调用方向 | admin_portal → mistake |
| 调用方 | OssAuditWorker.RunAuditAsync |
| 被调用方 | MistakeGrpcService |
| 功能说明 | 获取所有被错题引用的图片路径，用于 OSS 孤儿文件审计 |

**请求参数 — Empty**

无参数。

**响应参数 — GetAllReferencedImagePathsResponse**

| 参数名 | 必填/可选 | 数据类型 | 取值范围 | 业务含义 |
|--------|----------|---------|---------|---------|
| image_paths | 必填 | repeated string | — | 所有被引用的图片路径列表 |

**错误语义**

| 场景 | 说明 |
|------|------|
| 内部错误 | gRPC INTERNAL |

**幂等性**：幂等（只读查询）。

**兼容性注意事项**：当错题数据量增长时，此接口返回的路径列表可能较大，调用方需考虑内存和传输开销。

---

## 3. 枚举定义

### ReviewStatus

| 值 | 名称 | 业务含义 |
|----|------|---------|
| 0 | UNSPECIFIED | 未指定（不应使用） |
| 1 | PENDING_REVIEW | 待审核 |
| 2 | CONFIRMED | 已确认 |
| 3 | REJECTED | 已驳回 |

### ProcessAction

| 值 | 名称 | 业务含义 |
|----|------|---------|
| 0 | UNSPECIFIED | 未指定（不应使用） |
| 1 | CREATED | 创建 |
| 2 | ANALYZED | 分析 |
| 3 | REVIEW_CONFIRMED | 审核确认 |
| 4 | REVIEW_REJECTED | 审核驳回 |
| 5 | PRACTICED | 练习 |
| 6 | MASTERED | 已掌握 |

---

## 4. 公共消息类型

### PageMeta

| 参数名 | 必填/可选 | 数据类型 | 业务含义 |
|--------|----------|---------|---------|
| page | 必填 | int32 | 当前页码 |
| size | 必填 | int32 | 每页条数 |
| total_count | 必填 | int32 | 总记录数 |
| total_pages | 必填 | int32 | 总页数 |

### PendingReviewUploadDto

| 参数名 | 必填/可选 | 数据类型 | 业务含义 |
|--------|----------|---------|---------|
| source_upload_id | 必填 | string | 上传批次 ID |
| student_id | 必填 | string | 学生 ID |
| subject | 必填 | int32 | 科目编号 |
| grade | 必填 | int32 | 年级编号 |
| pending_count | 必填 | int32 | 该批次待审核条目数 |
| total_count | 必填 | int32 | 该批次总条目数 |
| earliest_created_at | 必填 | string | 最早创建时间 |

### ProcessLogDto

| 参数名 | 必填/可选 | 数据类型 | 业务含义 |
|--------|----------|---------|---------|
| id | 必填 | string | 日志 ID |
| mistake_item_id | 必填 | string | 关联错题 ID |
| action | 必填 | ProcessAction | 处理动作 |
| details | 可选 | string | 详细说明 |
| created_at | 必填 | string | 创建时间 |

---

## 5. 跨服务副作用汇总

| 触发接口 | 副作用类型 | 目标服务/资源 | 说明 |
|----------|-----------|-------------|------|
| SubmitMistakeUpload | ProcessLog 记录 | mistake 内部 | 记录 CREATED 动作 |
| ReviewMistakeItem | ProcessLog 记录 | mistake 内部 | 记录 REVIEW_CONFIRMED 或 REVIEW_REJECTED 动作 |
| AddMistakeItem | ProcessLog 记录 | mistake 内部 | 记录 CREATED 动作 |
| AddMistakePractice | ProcessLog 记录 | mistake 内部 | 记录 PRACTICED 动作 |
| AnalyzeMistakeType | ProcessLog 记录 | mistake 内部 | 记录 ANALYZED 动作 |
| CompleteUploadReview | OSS 文件迁移 | 对象存储 | 将图片从 uploads/ 复制到 mistakes/，删除 uploads/ 下的原文件 |
| CompleteUploadReview | student 回调 | student 服务 | 调用 student.RemoveImagesFromRecord 移除上传记录中的图片引用 |

**关键风险**：CompleteUploadReview 的 OSS 操作不具备事务性。如果 copy 成功但 delete 失败，会导致 uploads/ 和 mistakes/ 两处均存在文件；如果 student 回调失败，mistake 侧已完成审核但 student 侧记录未更新。调用方需设计补偿机制。

---

## 6. 幂等性与兼容性注意事项

### 幂等性矩阵

| 接口 | 幂等性 | 说明 |
|------|--------|------|
| SubmitMistakeUpload | ❌ 非幂等 | 每次调用创建新条目 |
| GetMistakeItem | ✅ 幂等 | 只读查询 |
| GetMistakeItemsByUpload | ✅ 幂等 | 只读查询 |
| GetMistakeItemList | ✅ 幂等 | 只读查询 |
| GetPendingReviewUploads | ✅ 幂等 | 只读查询 |
| ReviewMistakeItem | ⚠️ 非幂等但安全 | 仅 PendingReview 状态可审核，重复审核同状态会被拒绝 |
| AddMistakeItem | ❌ 非幂等 | 每次调用创建新条目 |
| UpdateMistakeItem | ❌ 非幂等 | 多次调用以最终一次为准 |
| DeleteMistakeItem | ✅ 幂等 | 不存在时返回 false |
| CompleteUploadReview | ❌ 非幂等 | OSS 操作不可回滚，需确保仅调用一次 |
| AddMistakePractice | ❌ 非幂等 | 每次调用创建新练习记录 |
| GetMistakePractices | ✅ 幂等 | 只读查询 |
| AnalyzeMistakeType | ❌ 非幂等 | 每次分析可能产生不同结果，且记录 ProcessLog |
| LogMistakeProcess | ❌ 非幂等 | 每次调用追加日志 |
| GetMistakeProcessHistory | ✅ 幂等 | 只读查询 |
| GetAllReferencedImagePaths | ✅ 幂等 | 只读查询 |

### 兼容性注意事项

1. **proto 字段编号**：新增字段必须使用新的字段编号，不得复用已删除字段的编号。
2. **枚举值扩展**：ReviewStatus 和 ProcessAction 新增值时，旧客户端应能安全忽略未知枚举值（gRPC 规范要求）。
3. **CompleteUploadReview 调用保障**：此接口的跨服务副作用不可回滚，调用方必须实现至少一次语义（at-least-once），建议通过业务层幂等键或状态检查避免重复调用。
4. **GetAllReferencedImagePaths 性能**：随着数据量增长，全量路径查询的响应体积会增大，未来可能需要引入分页或增量查询机制。
5. **ReviewMistakeItem 状态约束**：仅 PendingReview 状态的条目可被审核，调用方应在 UI 层做好状态判断，避免无效请求。
