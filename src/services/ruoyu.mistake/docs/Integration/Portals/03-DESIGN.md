# Mistake 与 Portals 集成 — 设计说明

## Portal 集成架构

```
┌──────────────────────────────────────────────────────────────────────┐
│                         Mistake gRPC Service                         │
│                     (http://localhost:5006)                          │
│                                                                      │
│  GetMistakeItemList  GetMistakeItem  GetMistakeItemsByUpload         │
│  UpdateMistakeItem   ReviewMistakeItem  DeleteMistakeItem            │
│  AddMistakeItem      CompleteUploadReview                            │
│  GetPendingReviewUploads  GetAllReferencedImagePaths                 │
└──────────┬───────────────────┬───────────────────┬──────────────────┘
           │                   │                   │
           │ gRPC              │ gRPC              │ gRPC
           ▼                   ▼                   ▼
┌──────────────────┐ ┌──────────────────┐ ┌──────────────────┐
│  admin_portal    │ │  teacher_portal  │ │  user_portal     │
│                  │ │                  │ │                  │
│ MistakeController│ │ MistakeController│ │ MistakeController│
│ OssAuditWorker   │ │                  │ │                  │
└────────┬─────────┘ └────────┬─────────┘ └──────────────────┘
         │                    │
         │ gRPC               │ gRPC
         ▼                    ▼
┌──────────────────┐ ┌──────────────────┐
│ Student          │ │ Student          │
│ ManagementGrpc   │ │ LearningGrpc     │
│ (GetStudentAsync)│ │ (MarkUpload...   │
│                  │ │  DeleteUpload...) │
└──────────────────┘ └──────────────────┘
```

## 各 Portal 的 gRPC 调用矩阵

| gRPC 方法 | admin_portal | teacher_portal | user_portal |
|-----------|:---:|:---:|:---:|
| GetMistakeItemList | ✅ | ✅ | ✅ |
| GetMistakeItem | ✅ | ✅ | ✅ |
| GetMistakeItemsByUpload | ✅ | ✅ | — |
| UpdateMistakeItem | ✅ | — | — |
| ReviewMistakeItem | — | ✅ | — |
| DeleteMistakeItem | — | ✅ | ✅ |
| AddMistakeItem | — | ✅ | — |
| CompleteUploadReview | — | ✅ | — |
| GetPendingReviewUploads | — | ✅ | — |
| GetAllReferencedImagePaths | ✅(OssAudit) | — | — |

## teacher_portal 审核流程设计

### 单条审核流程（ReviewItem）

```
ReviewItem(request)
│
├─ 参数校验：Id 非空，ReviewStatus ∈ {2, 3}
│
├─ gRPC: ReviewMistakeItem(id, reviewStatus, reviewerId, ...)
│  └─ 失败 → 返回 { success: false, message }
│
├─ ReviewStatus == 3（退回）
│  ├─ gRPC: GetMistakeItem(id) → sourceUploadId, studentId
│  ├─ gRPC: DeleteMistakeItem(id) → 删除该错题
│  ├─ gRPC: GetMistakeItemsByUpload(sourceUploadId)
│  │  └─ 检查剩余错题数量
│  └─ 若剩余 == 0 且 studentId 非空
│     └─ gRPC(Student): MarkUploadRecordAsReturned(recordId, studentId, "所有错题已退回")
│
└─ ReviewStatus == 2（确认）
   ├─ gRPC: GetMistakeItem(id) → sourceUploadId
   ├─ gRPC: GetMistakeItemsByUpload(sourceUploadId)
   │  └─ 统计 PendingReview 状态的条目数
   └─ 若 pendingCount == 0 且 totalItems > 0
      ├─ gRPC: CompleteUploadReview(sourceUploadId, reviewerId)
      └─ gRPC(Student): DeleteUploadRecordAfterReview(recordId, studentId)
```

### 批量审核流程（BatchReview）

```
BatchReview(request)
│
├─ 参数校验：Items 或 Ids 非空，ReviewStatus ∈ {2, 3}
│
├─ 逐条处理：
│  ├─ gRPC: ReviewMistakeItem(id, reviewStatus, ...)
│  │
│  ├─ ReviewStatus == 3（退回）
│  │  ├─ gRPC: GetMistakeItem(id) → sourceUploadId, studentId
│  │  ├─ gRPC: DeleteMistakeItem(id)
│  │  └─ 若该上传记录下无剩余错题
│  │     └─ gRPC(Student): MarkUploadRecordAsReturned(...)
│  │
│  └─ ReviewStatus == 2（确认）
│     └─ 收集 sourceUploadId → sourceUploadIds 集合
│
└─ ReviewStatus == 2 后处理（统一检查）：
   └─ 遍历 sourceUploadIds
      ├─ gRPC: GetMistakeItemsByUpload(sourceUploadId)
      ├─ 统计 PendingReview 条目数
      └─ 若 pendingCount == 0 且 totalItems > 0
         ├─ gRPC: CompleteUploadReview(sourceUploadId, reviewerId)
         └─ gRPC(Student): DeleteUploadRecordAfterReview(...)
```

### 退回流程（ReturnMistake）

```
ReturnMistake(id, studentId, returnReason)
│
├─ 参数校验：id 和 studentId 非空
│
├─ gRPC: GetMistakeItem(id) → sourceUploadId
│  └─ sourceUploadId 为空 → 返回 BadRequest
│
├─ gRPC: DeleteMistakeItem(id)
│  └─ 失败 → 返回 { success: false }
│
└─ gRPC(Student): MarkUploadRecordAsReturned(recordId, studentId, reason)
   └─ 失败仅记录日志，不影响退回结果
```

## 权限过滤设计

### teacher_portal 学科过滤

```
请求到达
│
├─ GetTeacherSubjects()
│  ├─ 从 JWT 获取 userId: ClaimTypes.NameIdentifier 或 "sub"
│  └─ TeacherDbService.GetSubjectsByUserId(userId) → List<int>
│
├─ 无学科参数时
│  └─ 使用教师全部学科作为过滤条件
│
├─ 有学科参数时
│  └─ 取请求学科 ∩ 教师学科
│     └─ 交集为空 → 返回空结果
│
└─ gRPC 请求中携带过滤后的学科列表
```

**注意**：`GetMistakeItemList` 接口的 `Subject` 字段不支持多学科，teacher_portal 采用 `Subject=-1`（不过滤）+ 内存过滤的方式实现多学科筛选。

### user_portal 学生过滤

- 通过 `studentId` 查询参数过滤
- `ReviewStatus` 固定为 `Confirmed`，学生只能看到已确认的错题
- 删除操作增加状态校验：`Confirmed` 状态不允许删除

### admin_portal 无过滤

- 管理员可查看所有数据
- 所有筛选参数均为可选

## Portal 层额外行为总结

### 退回后清理链

| 步骤 | 调用 | 失败处理 |
|------|------|----------|
| 1 | `DeleteMistakeItem(id)` | 记录日志，标记为 failedId |
| 2 | `GetMistakeItemsByUpload(sourceUploadId)` | 记录日志，跳过后续 |
| 3 | `MarkUploadRecordAsReturned(recordId, studentId, ...)` | 仅记录日志，不影响审核结果 |

### 确认后自动完成审核链

| 步骤 | 条件 | 调用 | 失败处理 |
|------|------|------|----------|
| 1 | — | `GetMistakeItemsByUpload(sourceUploadId)` | 记录日志，跳过 |
| 2 | pendingCount == 0 且 totalItems > 0 | `CompleteUploadReview(sourceUploadId, reviewerId)` | 记录日志，跳过 |
| 3 | CompleteUploadReview 成功且含 StudentId | `DeleteUploadRecordAfterReview(recordId, studentId)` | 仅记录日志 |

### admin_portal 学生姓名补充

- 每次查询错题后，提取所有不重复的 `studentId`
- 逐个调用 `GetStudentAsync(studentId)` 获取学生姓名
- 调用失败时降级为使用 `studentId` 作为显示名
