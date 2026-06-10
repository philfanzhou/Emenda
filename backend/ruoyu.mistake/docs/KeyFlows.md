# ruoyu.mistake 关键业务流程

## 流程1: 错题提交流程

**触发条件**: ruoyu.student 的 UploadAnalysisWorker 完成 VL 图像分析后，或 GrpcUploadDispatcher 直接分发
**参与服务**: ruoyu.student → ruoyu.mistake
**数据流转**: 上传记录元数据 + 图片路径 → mistake_item(PendingReview) + process_log(Created)

```
 student (UploadAnalysisWorker)               mistake
       │                                         │
       │  SubmitMistakeUpload                    │
       │  (student_id, subject, grade,           │
       │   image_paths, source_upload_id)        │
       │────────────────────────────────────────▶│
       │                                         │ 1. 参数校验
       │                                         │ 2. 创建 MistakeItem
       │                                         │    (ReviewStatus=PendingReview)
       │                                         │ 3. 记录 ProcessLog(Created)
       │                                         │ 4. SaveChanges
       │                                         │
       │  Response: created_item_ids             │
       │◀────────────────────────────────────────│
       │                                         │
       │  标记上传记录 Completed                  │
       │                                         │
```

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
