# Mistake 与 Portals 集成 — 接口规格

## Ruoyu.Admin

路由前缀：`api/admin/mistakes`

### GET /api/admin/mistakes — 分页查询错题列表

**gRPC 调用**：`GetMistakeItemList`

**请求参数**（Query）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| studentId | string | 否 | 学生ID筛选 |
| subject | int | 否 | 学科筛选 |
| grade | int | 否 | 年级筛选 |
| reviewStatus | int | 否 | 审核状态筛选（ReviewStatus 枚举值） |
| page | int | 否 | 页码，默认 1 |
| size | int | 否 | 每页条数，默认 20，最大 100 |

**gRPC 请求**：

| 字段 | 映射 |
|------|------|
| StudentId | studentId ?? "" |
| Subject | subject ?? 0 |
| Grade | grade ?? 0 |
| ReviewStatus | reviewStatus（枚举转换） |
| Page | page |
| Size | size |

**响应**：包含 items 列表（补充 studentName）、分页信息（total, page, pageSize, totalPages）

**额外行为**：查询结果中通过 `GetStudentAsync` 补充学生姓名

**权限**：无特殊控制

---

### GET /api/admin/mistakes/{id} — 查询单条错题

**gRPC 调用**：`GetMistakeItem`

**请求参数**（Route）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| id | string | 是 | 错题ID |

**gRPC 请求**：`IdRequest { Id = id }`

**响应**：错题详情（含 sourceRegions、boundingBox），补充 studentName

**额外行为**：通过 `GetStudentAsync` 补充学生姓名

**权限**：无特殊控制

---

### GET /api/admin/mistakes/by-upload/{uploadId} — 按上传记录查询

**gRPC 调用**：`GetMistakeItemsByUpload`

**请求参数**（Route）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| uploadId | string | 是 | 上传记录ID |

**gRPC 请求**：`GetMistakeItemsByUploadRequest { SourceUploadId = uploadId }`

**响应**：错题列表（补充 studentName）、总数

**额外行为**：通过 `GetStudentAsync` 补充学生姓名

**权限**：无特殊控制

---

### PUT /api/admin/mistakes/{id} — 更新错题

**gRPC 调用**：`UpdateMistakeItem`

**请求参数**（Route + Body）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| id | string | 是 | 错题ID（Route） |
| StudentId | string | 否 | 新学生ID |
| Subject | int | 否 | 新学科 |
| Grade | int | 否 | 新年级 |

**gRPC 请求**：`UpdateMistakeItemRequest { Id, StudentId, Subject, Grade }`

**响应**：更新后的错题信息，补充 studentName

**额外行为**：通过 `GetStudentAsync` 补充学生姓名

**权限**：无特殊控制

---

### POST /api/admin/mistakes/migrate-images/{id} — 手动迁移图片

**gRPC 调用**：`GetMistakeItem` + `UpdateMistakeItem`

**请求参数**（Route）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| id | string | 是 | 错题ID |

**处理逻辑**：

1. 调用 `GetMistakeItem` 获取当前错题信息
2. 筛选 `uploads/` 前缀的图片路径
3. 逐个执行 `IOssService.CopyObjectAsync(oldPath, newPath)` + `IOssService.DeleteAsync(oldPath)`
4. 路径映射：`"mistakes" + oldPath.Substring("uploads".Length)`
5. 使用 `pathMapping` 字典缓存已迁移路径
6. 调用 `UpdateMistakeItem` 更新图片路径

**响应**：`{ success, message, migratedCount }`

**权限**：无特殊控制

---

## teacher_portal

路由前缀：`api/mistake`

### GET /api/mistake/pending-reviews — 待审核上传记录列表

**gRPC 调用**：`GetPendingReviewUploads`

**请求参数**（Query）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| page | int | 否 | 页码，默认 1 |
| pageSize | int | 否 | 每页条数，默认 10，最大 50 |
| subjects | string | 否 | 学科筛选，逗号分隔（如 "1,2,3"） |

**gRPC 请求**：`GetPendingReviewUploadsRequest { Page, Size, Subjects[] }`

**权限过滤**：
1. 获取教师学科列表 `GetTeacherSubjects()`
2. 若未指定 subjects 参数，使用教师全部学科
3. 若指定了 subjects，取与教师学科的交集
4. 交集为空时返回空结果

**响应**：`PendingReviewListResponse { Success, Total, TotalPages, Page, PageSize, Data[] }`

---

### GET /api/mistake/review-detail/{sourceUploadId} — 审核详情

**gRPC 调用**：`GetMistakeItemsByUpload`

**请求参数**（Route）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| sourceUploadId | string | 是 | 上传记录ID |

**gRPC 请求**：`GetMistakeItemsByUploadRequest { SourceUploadId }`

**响应**：`UploadReviewDetailResponse { Success, SourceUploadId, Items[] }`

---

### POST /api/mistake/review-item — 审核单条错题

**gRPC 调用**：`ReviewMistakeItem` + 后续清理逻辑

**请求参数**（Body）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| Id | string | 是 | 错题ID |
| ReviewStatus | int | 是 | 审核结果：2=确认，3=退回 |
| RootCause | string | 否 | 错误根因（AI 生成，老师可修改） |
| Grade | int | 否 | 年级修正 |
| Subject | int | 否 | 学科修正 |

**gRPC 请求**：`ReviewMistakeItemRequest { Id, ReviewStatus, ReviewerId, RootCause, Grade, Subject }`

**ReviewerId 来源**：`User.FindFirst(ClaimTypes.NameIdentifier)` 或 `User.FindFirst("sub")`

**退回后（ReviewStatus=3）额外行为**：

1. `GetMistakeItem` → 获取 sourceUploadId 和 studentId
2. `DeleteMistakeItem` → 删除该错题
3. `GetMistakeItemsByUpload` → 检查上传记录下是否还有剩余错题
4. 若无剩余错题 → `studentClient.MarkUploadRecordAsReturnedAsync`

**确认后（ReviewStatus=2）额外行为**：

1. `GetMistakeItem` → 获取 sourceUploadId
2. `GetMistakeItemsByUpload` → 检查是否还有待审核条目
3. 若无待审核条目 → `CompleteUploadReview` → `studentClient.DeleteUploadRecordAfterReviewAsync`

---

### POST /api/mistake/batch-review — 批量审核

**gRPC 调用**：`ReviewMistakeItem` × N + 后续清理逻辑

**请求参数**（Body）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| Ids | List\<string\> | 否* | 错题ID列表（与 Items 二选一） |
| Items | List\<BatchReviewItemRequest\> | 否* | 错题列表（含 Id, Subject, Grade），与 Ids 二选一 |
| ReviewStatus | int | 是 | 审核结果：2=确认，3=退回 |
| RootCause | string | 否 | 错误根因（AI 生成，老师可修改） |

**BatchReviewItemRequest**：

| 字段 | 类型 | 说明 |
|------|------|------|
| Id | string | 错题ID |
| Subject | int? | 学科修正 |
| Grade | int? | 年级修正 |

**额外行为**：与单条审核相同，退回后清理、确认后自动完成审核。批量确认时，收集所有 sourceUploadId，统一检查每个上传记录是否全部审核完成。

**响应**：`BatchReviewResponse { Success, Message, ProcessedCount, FailedCount, FailedIds[] }`

---

### GET /api/mistake/{id} — 错题详情

**gRPC 调用**：`GetMistakeItem`

**请求参数**（Route）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| id | string | 是 | 错题ID |

**gRPC 请求**：`IdRequest { Id = id }`

**响应**：`UploadReviewDetailResponse { Success, SourceUploadId, Items[] }`（单条包装为列表）

---

### POST /api/mistake/{id}/return — 退回错题

**gRPC 调用**：`GetMistakeItem` + `DeleteMistakeItem` + `MarkUploadRecordAsReturned`

**请求参数**（Route + Query + Body）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| id | string | 是 | 错题ID（Route） |
| studentId | string | 是 | 学生ID（Query） |
| ReturnReason | string | 否 | 退回原因（Body） |

**处理逻辑**：

1. `GetMistakeItem` → 获取 sourceUploadId
2. `DeleteMistakeItem` → 删除错题
3. `studentClient.MarkUploadRecordAsReturnedAsync` → 标记上传记录为已退回

**响应**：`{ success, message }`

---

### POST /api/mistake/add-item — 手动添加错题

**gRPC 调用**：`AddMistakeItem`

**请求参数**（Body）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| StudentId | string | 是 | 学生ID |
| Subject | int | 是 | 学科 |
| Grade | int | 是 | 年级 |
| SourceUploadId | string | 是 | 上传记录ID |
| SourceRegions | List\<SourceRegionItem\> | 是 | 图片区域列表 |
| Comments | string | 否 | 备注 |
| QuestionNumber | string | 否 | 题号 |
| QuestionText | string | 否 | 题目文本 |
| StudentAnswer | string | 否 | 学生答案 |

**SourceRegionItem**：

| 字段 | 类型 | 说明 |
|------|------|------|
| SourceImagePath | string | 图片路径 |
| BoundingBox | BoundingBoxItem? | 裁剪区域 |

**BoundingBoxItem**：

| 字段 | 类型 | 说明 |
|------|------|------|
| X1, Y1, X2, Y2 | int | 裁剪坐标 |

**响应**：`{ success, id }`

---

### POST /api/mistake/complete-review — 完成审核

**gRPC 调用**：`CompleteUploadReview` + `DeleteUploadRecordAfterReviewAsync`

**请求参数**（Body）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| SourceUploadId | string | 是 | 上传记录ID |

**gRPC 请求**：`CompleteUploadReviewRequest { SourceUploadId, ReviewerId }`

**额外行为**：若 `CompleteUploadReview` 返回成功且含 StudentId → 调用 `studentClient.DeleteUploadRecordAfterReviewAsync`

**响应**：`{ success, message }`

---

### GET /api/mistake/list — 错题列表

**gRPC 调用**：`GetMistakeItemList`

**请求参数**（Query）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| studentId | string | 是 | 学生ID |
| page | int | 否 | 页码，默认 1 |
| pageSize | int | 否 | 每页条数，默认 10，最大 50 |
| subject | string | 否 | 学科筛选，逗号分隔 |

**gRPC 请求**：`GetMistakeItemListRequest { StudentId, Page, Size, Subject=-1, ReviewStatus=Confirmed }`

**权限过滤**：
1. 获取教师学科列表 `GetTeacherSubjects()`
2. gRPC 返回结果按教师学科过滤（内存过滤）
3. 若指定了 subject 参数，进一步按选中学科过滤

**响应**：`MistakeListResponse { Success, Total, TotalPages, Page, PageSize, Data[] }`

---

### GET /api/mistake/pending-items — 待审核条目

**gRPC 调用**：`GetPendingReviewUploads` + `GetMistakeItemsByUpload` × N

**请求参数**（Query）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| page | int | 否 | 页码，默认 1 |
| pageSize | int | 否 | 每页条数，默认 20，最大 100 |
| subjects | string | 否 | 学科筛选，逗号分隔 |

**处理逻辑**：

1. 获取教师学科列表，过滤权限
2. `GetPendingReviewUploads` → 获取待审核上传记录
3. 对每个上传记录调用 `GetMistakeItemsByUpload` → 获取 PendingReview 状态的错题
4. 按 SourceUploadId 和 ItemId 去重
5. 内存分页

**响应**：`PendingReviewItemListResponse { Success, Total, TotalPages, Page, PageSize, Data[] }`

---

## user_portal

路由前缀：`api/mistake`

### GET /api/mistake/list — 学生错题列表

**gRPC 调用**：`GetMistakeItemList`

**请求参数**（Query）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| studentId | string | 是 | 学生ID |
| page | int | 否 | 页码，默认 1 |
| pageSize | int | 否 | 每页条数，默认 10，最大 50 |
| subject | string | 否 | 学科名称（如 "数学"） |
| grade | string | 否 | 年级名称（如 "高一"） |

**gRPC 请求**：`GetMistakeItemListRequest { StudentId, Page, Size, Subject, Grade, ReviewStatus=Confirmed }`

**关键行为**：`ReviewStatus` 固定为 `Confirmed`，只返回已确认的错题

**subject/grade 映射**：通过 `Mappings.NameToSubject` / `Mappings.NameToGrade` 将名称转为枚举值

**响应**：`MistakeListResponse { Success, Total, TotalPages, Page, PageSize, Data[] }`

**权限**：通过 studentId 参数过滤

---

### GET /api/mistake/{id} — 错题详情

**gRPC 调用**：`GetMistakeItem`

**请求参数**（Route）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| id | string | 是 | 错题ID |

**gRPC 请求**：`IdRequest { Id = id }`

**响应**：`MistakeDetailResponse { Success, Data: MistakeDetailItem }`

**错误处理**：gRPC NotFound → 返回 `{ Success=false, Message="错题不存在或无权操作" }`

---

### DELETE /api/mistake/{id} — 删除错题

**gRPC 调用**：`GetMistakeItem` + `DeleteMistakeItem`

**请求参数**（Route）：

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| id | string | 是 | 错题ID |

**处理逻辑**：

1. `GetMistakeItem` → 获取当前审核状态
2. 若 `ReviewStatus == Confirmed` → 拒绝删除，返回 `{ Success=false, Message="该错题已确认，无法删除" }`
3. 否则 → `DeleteMistakeItem` → 删除

**响应**：`MistakeDeleteResponse { Success, Message }`

**权限**：仅非 Confirmed 状态可删除
