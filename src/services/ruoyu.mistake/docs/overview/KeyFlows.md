# ruoyu.mistake 关键业务流程

## 流程1: 错题自动分析流程（UploadPollingWorker）

**触发条件**: UploadPollingWorker 每 60 秒轮询 Student 服务的 Processing 状态上传记录
**参与服务**: ruoyu.mistake(Worker) → ruoyu.student, VL API, OSS
**数据流转**: 上传记录图片 → VL 分组分析 → MistakeItem(PendingReview) + OSS 迁移(uploads/→mistakes/) → 通知 Student 删除已处理图片

```
 mistake (UploadPollingWorker)        student           VL API          OSS
       │                                │                 │              │
       │ GetProcessingUploadRecords     │                 │              │
       │───────────────────────────────▶│                 │              │
       │◀───────────────────────────────│                 │              │
       │                                │                 │              │
       │ 【幂等检查】                    │                 │              │
       │ GetMistakeItemsByUpload(uploadId)                │              │
       │ (内部调用 DomainService)        │                 │              │
       │ → 提取已处理图片路径             │                 │              │
       │ → 计算未处理图片 = 全部 - 已处理  │                 │              │
       │                                │                 │              │
       │ 未处理为空？                     │                 │              │
       │  ├─ 是 → 跳过 VL/OSS，          │                 │              │
       │  │      直接通知删除全部图片      │                 │              │
       │  └─ 否 → 继续                   │                 │              │
       │                                │                 │              │
       │ VL Analyze(未处理图片)          │                 │              │
       │────────────────────────────────────────────────▶│              │
       │◀────────────────────────────────────────────────│              │
       │ (分组结果: subject, grade, rootCause)            │              │
       │                                │                 │              │
       │ SubmitMistakeUpload(未处理图片)  │                 │              │
       │ (内部调用 DomainService)        │                 │              │
       │                                │                 │              │
       │ OSS Copy uploads/→mistakes/    │                 │              │
       │ (仅未处理图片)                  │                 │─────────────▶│
       │ OSS Delete uploads/            │                 │─────────────▶│
       │                                │                 │              │
       │ 更新 MistakeItem 路径           │                 │              │
       │                                │                 │              │
       │ RemoveProcessedImages          │                 │              │
       │ (已处理+新处理的所有图片)        │                 │              │
       │───────────────────────────────▶│                 │              │
       │◀───────────────────────────────│                 │              │
       │                                │                 │              │
       │                                │ 图片删完→自动删除记录            │
```

### 幂等保证

UploadPollingWorker 在处理每条记录前，先查询该 uploadId 下已有的 MistakeItem，提取已处理图片路径，只对未处理图片执行 VL 分析、MistakeItem 创建和 OSS 迁移。这确保：

1. **不会重复创建 MistakeItem**：已处理的图片不会被再次提交
2. **不会重复迁移 OSS**：已迁移的图片不会被再次 copy/delete
3. **存量脏数据自动清理**：之前因 bug 遗留的"已处理但未通知删除"的记录，下次轮询时会被识别并通知 Student 删除
4. **部分失败安全**：如果某次处理只完成了部分图片，下次轮询会只处理剩余图片
5. **移除回调可重放（IKJEY6）**：Student 侧删除记录时写入删除墓碑；响应丢失或 Worker 重试时同一移除请求得到幂等成功（Info 日志确认），未知记录（无记录且无墓碑）记 Warning 并要求人工核查，不重试。协议细节见 [Integration/Student.md](../Integration/Student.md) 的「移除回调三态语义」

## 流程2: 错题审核流程

**触发条件**: 教师在 teacher_portal 发起审核操作
**参与服务**: teacher_portal → ruoyu.mistake, OSS
**数据流转**: PendingReview 错题 → Confirmed/Rejected → OSS 图片迁移(uploads/→mistakes/)

```
 teacher_portal              mistake                    OSS
      │                        │                         │
      │ GetPendingReviewUploads│                         │
      │───────────────────────▶│                         │
      │◀───────────────────────│                         │
      │                        │                         │
      │ GetMistakeItemsByUpload│                         │
      │───────────────────────▶│                         │
      │◀───────────────────────│                         │
      │                        │                         │
      │ ReviewMistakeItem      │                         │
      │ (逐条: Confirmed/      │                         │
      │  Rejected, 可修正      │                         │
      │  grade/subject)        │                         │
      │───────────────────────▶│                         │
      │                        │ 记录 ProcessLog         │
      │                        │ (ReviewConfirmed/       │
      │                        │  ReviewRejected)        │
      │◀───────────────────────│                         │
      │                        │                         │
      │ CompleteUploadReview   │                         │
      │ (source_upload_id)     │                         │
      │───────────────────────▶│                         │
      │                        │                         │
      │                        │ 校验: 无 PendingReview  │
      │                        │                         │
      │                        │ Confirmed item:         │
      │                        │  copy uploads/→mistakes/│
      │                        │────────────────────────▶│
      │                        │                         │
      │                        │  delete uploads/        │
      │                        │────────────────────────▶│
      │                        │                         │
      │                        │ 更新 source_regions 路径│
      │                        │                         │
      │ Response: success,     │                         │
      │          student_id    │                         │
      │◀───────────────────────│                         │
      │                        │                         │
```

## 流程3: 错题练习流程

**触发条件**: 学生对已确认的错题进行练习
**参与服务**: 前端/外部 → ruoyu.mistake
**数据流转**: Confirmed 错题 → mistake_practice + process_log(Practiced)

```
 前端/外部                   mistake
      │                        │
      │ AddMistakePractice     │
      │ (mistake_item_id,      │
      │  student_id,           │
      │  is_correct, ...)      │
      │───────────────────────▶│
      │                        │ 1. 校验错题存在且为 Confirmed
      │                        │ 2. 创建 MistakePractice 记录
      │                        │ 3. 记录 ProcessLog(Practiced)
      │                        │ 4. SaveChanges
      │                        │
      │ Response: PracticeDto  │
      │◀───────────────────────│
      │                        │
      │ GetMistakePractices    │
      │ (mistake_item_id)      │
      │───────────────────────▶│
      │                        │ 查询所有练习记录
      │ Response: practices[]  │
      │◀───────────────────────│
      │                        │
```
