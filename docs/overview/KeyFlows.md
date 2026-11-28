# ruoyu.mistake 关键业务流程

## 流程1：来源入库与旧轮询

开启 `Mistake:SourceIntake:Enabled` 时，Worker scan 只提供 source/revision hint；共同 intake 服务取得 Student snapshot、持久首个模式/lease，核验原字节后构造完整自动计划。一个短提交物化全部 items、每项 Created 日志和结果映射；重复直接查询已保存结果，来源始终 SourceRetained。两个手动入口使用相同组与请求身份，manual owner 允许后继合法区域，automatic owner 的完整计划冻结。唯一模型见来源入库。

关闭时旧未受管来源仍采用查询已有路径→分析未处理图→逐组提交→copy/delete→remove 回调的历史流程。这只是进程级检查，没有跨实例唯一约束，也不能保证部分失败或 OSS/数据库/Student 原子性；legacy 重复与 durable forward handoff 另归原父任务。任何已受管来源都在进入这条流程前拒绝，不因开关关闭恢复源图 mover。

## 流程2: 错题审核流程

**触发条件**: 教师在 teacher_portal 发起审核操作
**参与服务**: teacher_portal → ruoyu.mistake, OSS
**数据流转**: Confirm 只更新审核状态；受管来源 Reject/CompleteReview 明确409，历史未受管退回/搬图仍属 best-effort。下图仅描述 legacy CompleteReview。

```
 teacher_portal              mistake                    OSS
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
