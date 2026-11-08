# Mistake 与 Portals 集成 — 测试用例

## Ruoyu.Admin

### TC-ADM-001 分页查询错题列表

**前置条件**：Mistake gRPC 服务可用，数据库中存在错题数据

**步骤**：
1. GET /api/admin/mistakes?page=1&size=10
2. 验证返回 items 列表包含 studentName 字段
3. 验证分页信息（total, page, pageSize, totalPages）

**预期结果**：
- 返回 200，items 中每条记录包含 studentName
- 若 GetStudentAsync 失败，studentName 降级为 studentId

### TC-ADM-002 按学科/年级/状态筛选

**步骤**：
1. GET /api/admin/mistakes?subject=1&grade=1&reviewStatus=2
2. 验证 gRPC 请求中 Subject=1, Grade=1, ReviewStatus=Confirmed

**预期结果**：返回筛选后的错题列表

### TC-ADM-003 查询单条错题

**步骤**：
1. GET /api/admin/mistakes/{id}
2. 验证返回详情包含 sourceRegions 和 boundingBox
3. 验证 studentName 已补充

**预期结果**：返回完整的错题详情

### TC-ADM-004 按上传记录查询

**步骤**：
1. GET /api/admin/mistakes/by-upload/{uploadId}
2. 验证返回该上传记录下的所有错题

**预期结果**：返回错题列表，total 为实际条目数

### TC-ADM-005 更新错题

**步骤**：
1. PUT /api/admin/mistakes/{id}，Body: { StudentId, Subject, Grade }
2. 验证 gRPC UpdateMistakeItemRequest 正确传递
3. 验证返回更新后的数据含 studentName

**预期结果**：错题信息更新成功

### TC-ADM-006 手动迁移图片 — 全部需迁移

**前置条件**：错题的 SourceRegions 中存在 uploads/ 前缀路径

**步骤**：
1. POST /api/admin/mistakes/migrate-images/{id}
2. 验证 CopyObjectAsync 和 DeleteAsync 被调用
3. 验证 UpdateMistakeItem 更新了路径
4. 验证返回 migratedCount > 0

**预期结果**：图片从 uploads/ 迁移到 mistakes/，路径更新成功

### TC-ADM-007 手动迁移图片 — 无需迁移

**前置条件**：错题的 SourceRegions 中所有路径已是 mistakes/ 前缀

**步骤**：
1. POST /api/admin/mistakes/migrate-images/{id}

**预期结果**：返回 `{ success: true, message: "所有图片路径已在 mistakes 下，无需迁移", migratedCount: 0 }`

### TC-ADM-008 手动迁移图片 — 单个失败

**前置条件**：某个图片的 CopyObjectAsync 抛出异常

**步骤**：
1. POST /api/admin/mistakes/migrate-images/{id}

**预期结果**：返回 500，包含失败图片路径信息

### TC-ADM-009 分页参数边界

**步骤**：
1. GET /api/admin/mistakes?page=0&size=0 → 验证修正为 page=1, size=10
2. GET /api/admin/mistakes?size=200 → 验证修正为 size=100

**预期结果**：参数自动修正到合法范围

### TC-ADM-010 OssAuditWorker 调用 GetAllReferencedImagePaths

**前置条件**：Mistake 服务可用

**步骤**：
1. 触发 OssAuditWorker
2. 验证调用了 GetAllReferencedImagePaths
3. 验证返回的路径被用于僵尸对象检测

**预期结果**：Mistake 引用路径与 Student 引用路径合并后，正确识别僵尸对象

### TC-ADM-011 OssAuditWorker — Mistake 服务不可用

**前置条件**：Mistake gRPC 服务不可用

**步骤**：
1. 触发 OssAuditWorker
2. 验证 Mistake 服务不可用时审计仍可执行（仅使用 Student 引用路径）

**预期结果**：审计完成，但日志中记录 Mistake 服务不可用警告，可能存在误报

## teacher_portal

### TC-TCH-001 待审核上传记录列表

**步骤**：
1. GET /api/mistake/pending-reviews?page=1&pageSize=10
2. 验证 gRPC 请求中 Subjects 为教师负责的学科列表

**预期结果**：返回教师学科范围内的待审核记录

### TC-TCH-002 学科过滤 — 指定学科

**步骤**：
1. GET /api/mistake/pending-reviews?subjects=1,2
2. 验证请求学科与教师学科的交集作为过滤条件

**预期结果**：仅返回交集学科下的待审核记录

### TC-TCH-003 学科过滤 — 无权限学科

**步骤**：
1. GET /api/mistake/pending-reviews?subjects=99（教师不负责的学科）

**预期结果**：返回空列表，Total=0

### TC-TCH-004 审核详情

**步骤**：
1. GET /api/mistake/review-detail/{sourceUploadId}
2. 验证返回该上传记录下所有错题的审核状态

**预期结果**：返回完整的审核详情列表

### TC-TCH-005 单条审核 — 确认

**前置条件**：上传记录下有多条待审核错题

**步骤**：
1. POST /api/mistake/review-item，Body: { Id, ReviewStatus=2 }
2. 验证 ReviewMistakeItem 被调用
3. 验证检查了上传记录下是否还有待审核条目
4. 若仍有待审核条目，不触发 CompleteUploadReview

**预期结果**：审核成功，不触发自动完成审核

### TC-TCH-006 单条审核 — 确认最后一条

**前置条件**：上传记录下仅剩一条待审核错题

**步骤**：
1. POST /api/mistake/review-item，Body: { Id, ReviewStatus=2 }
2. 验证 ReviewMistakeItem 被调用
3. 验证 GetMistakeItemsByUpload 返回无 PendingReview 条目
4. 验证 CompleteUploadReview 被调用
5. 验证 DeleteUploadRecordAfterReview 被调用

**预期结果**：审核成功，自动完成审核，上传记录被删除

### TC-TCH-007 单条审核 — 退回

**前置条件**：上传记录下有多条错题

**步骤**：
1. POST /api/mistake/review-item，Body: { Id, ReviewStatus=3 }
2. 验证 ReviewMistakeItem 被调用
3. 验证 DeleteMistakeItem 被调用
4. 验证 GetMistakeItemsByUpload 检查剩余错题
5. 若仍有剩余错题，不调用 MarkUploadRecordAsReturned

**预期结果**：错题被删除，上传记录保留

### TC-TCH-008 单条审核 — 退回最后一条

**前置条件**：上传记录下仅剩一条错题

**步骤**：
1. POST /api/mistake/review-item，Body: { Id, ReviewStatus=3 }
2. 验证 DeleteMistakeItem 被调用
3. 验证 GetMistakeItemsByUpload 返回空列表
4. 验证 MarkUploadRecordAsReturned 被调用

**预期结果**：错题被删除，上传记录标记为已退回

### TC-TCH-009 批量审核 — 确认

**步骤**：
1. POST /api/mistake/batch-review，Body: { Items: [{Id, Subject, Grade}], ReviewStatus=2 }
2. 验证每条都调用了 ReviewMistakeItem
3. 验证收集了 sourceUploadIds
4. 验证统一检查了每个上传记录的审核状态
5. 若全部审核完成，验证 CompleteUploadReview 和 DeleteUploadRecordAfterReview 被调用

**预期结果**：返回 BatchReviewResponse，含 ProcessedCount 和 FailedIds

### TC-TCH-010 批量审核 — 退回

**步骤**：
1. POST /api/mistake/batch-review，Body: { Ids: ["id1", "id2"], ReviewStatus=3 }
2. 验证每条都调用了 ReviewMistakeItem 和 DeleteMistakeItem
3. 验证 MarkUploadRecordAsReturned 在适当条件下被调用

**预期结果**：所有退回的错题被删除，空的上传记录标记为已退回

### TC-TCH-011 批量审核 — 参数校验

**步骤**：
1. POST /api/mistake/batch-review，Body: { ReviewStatus=2 }（无 Items 和 Ids）
2. 验证返回 400，Message: "未选择任何错题"

**步骤**：
1. POST /api/mistake/batch-review，Body: { Ids: ["id1"], ReviewStatus=1 }
2. 验证返回 400，Message: "审核结果只能是确认(2)或退回(3)"

### TC-TCH-012 退回错题

**步骤**：
1. POST /api/mistake/{id}/return?studentId=xxx，Body: { ReturnReason: "图片不清晰" }
2. 验证 GetMistakeItem → DeleteMistakeItem → MarkUploadRecordAsReturned

**预期结果**：错题被删除，上传记录标记为已退回，原因包含 ReturnReason

### TC-TCH-013 退回错题 — 缺少参数

**步骤**：
1. POST /api/mistake/{id}/return（无 studentId）
2. 验证返回 400

### TC-TCH-014 手动添加错题

**步骤**：
1. POST /api/mistake/add-item，Body: { StudentId, Subject, Grade, SourceUploadId, SourceRegions, Comments }
2. 验证 gRPC AddMistakeItemRequest 正确传递

**预期结果**：返回 `{ success: true, id: "..." }`

### TC-TCH-015 完成审核

**步骤**：
1. POST /api/mistake/complete-review，Body: { SourceUploadId }
2. 验证 CompleteUploadReview 被调用
3. 验证 DeleteUploadRecordAfterReview 被调用

**预期结果**：审核完成，上传记录被删除

### TC-TCH-016 错题列表 — 学科过滤

**步骤**：
1. GET /api/mistake/list?studentId=xxx
2. 验证 gRPC 请求 ReviewStatus=Confirmed
3. 验证返回结果按教师学科过滤

**预期结果**：仅返回教师学科范围内的已确认错题

### TC-TCH-017 错题列表 — 缺少 studentId

**步骤**：
1. GET /api/mistake/list（无 studentId）

**预期结果**：返回 `{ Success: false, Message: "缺少学生ID" }`

### TC-TCH-018 待审核条目 — 去重

**步骤**：
1. GET /api/mistake/pending-items
2. 验证按 SourceUploadId 和 ItemId 去重
3. 验证仅返回 PendingReview 状态的条目

**预期结果**：无重复条目

### TC-TCH-019 gRPC 服务不可用

**前置条件**：Mistake gRPC 服务不可用

**步骤**：
1. 调用任意接口

**预期结果**：返回 502 Bad Gateway

## user_portal

### TC-USR-001 学生错题列表

**步骤**：
1. GET /api/mistake/list?studentId=xxx&page=1&pageSize=10
2. 验证 gRPC 请求 ReviewStatus=Confirmed
3. 验证返回结果仅含 Confirmed 状态的错题

**预期结果**：返回已确认的错题列表

### TC-USR-002 学科/年级筛选

**步骤**：
1. GET /api/mistake/list?studentId=xxx&subject=数学&grade=高一
2. 验证名称通过 Mappings 转换为枚举值

**预期结果**：返回筛选后的错题列表

### TC-USR-003 缺少 studentId

**步骤**：
1. GET /api/mistake/list（无 studentId）

**预期结果**：返回 400，Message: "缺少学生ID"

### TC-USR-004 错题详情

**步骤**：
1. GET /api/mistake/{id}
2. 验证返回详情含 ImageUrls 和 ThumbnailUrls

**预期结果**：返回错题详情

### TC-USR-005 错题详情 — 不存在

**步骤**：
1. GET /api/mistake/{不存在的id}

**预期结果**：返回 `{ Success: false, Message: "错题不存在或无权操作" }`

### TC-USR-006 删除错题 — 非 Confirmed 状态

**前置条件**：错题状态为 PendingReview

**步骤**：
1. DELETE /api/mistake/{id}
2. 验证先调用 GetMistakeItem 获取状态
3. 验证状态非 Confirmed，调用 DeleteMistakeItem

**预期结果**：返回 `{ Success: true, Message: "删除成功" }`

### TC-USR-007 删除错题 — Confirmed 状态

**前置条件**：错题状态为 Confirmed

**步骤**：
1. DELETE /api/mistake/{id}
2. 验证 GetMistakeItem 返回 Confirmed 状态
3. 验证不调用 DeleteMistakeItem

**预期结果**：返回 `{ Success: false, Message: "该错题已确认，无法删除" }`

### TC-USR-008 删除错题 — 不存在

**步骤**：
1. DELETE /api/mistake/{不存在的id}

**预期结果**：返回 `{ Success: false, Message: "错题不存在或无权操作" }`

### TC-USR-009 gRPC 服务不可用

**前置条件**：Mistake gRPC 服务不可用

**步骤**：
1. 调用任意接口

**预期结果**：返回 502 Bad Gateway
