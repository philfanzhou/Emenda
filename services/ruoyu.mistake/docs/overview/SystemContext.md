# ruoyu.mistake 系统上下文

ruoyu.mistake 是 Ruoyu.Study 教育平台的错题本 gRPC 微服务，负责错题记录的全生命周期管理——从学生上传图片自动创建待审核错题、教师逐条审核确认/驳回、审核完成后触发 OSS 图片迁移，到错题练习追踪与类型分析。本服务是错题业务域的唯一数据权威，不调用其他业务微服务，仅通过 gRPC 对外暴露能力。

## 系统上下文图

```
                          ┌──────────────────┐
                          │  admin_portal    │
                          │  (管理后台)       │
                          └────────┬─────────┘
                                   │ gRPC :5006
                                   │ GetMistakeItemList, GetMistakeItem,
                                   │ GetMistakeItemsByUpload, UpdateMistakeItem,
                                   │ GetAllReferencedImagePaths
                                   ▼
┌──────────────┐    gRPC     ┌──────────────────────────────┐     S3 API    ┌──────────────┐
│              │────────────▶│                              │─────────────▶│              │
│  ruoyu.      │  :5006      │       ruoyu.mistake          │              │  MinIO /     │
│  student     │             │   (错题本 gRPC 微服务)        │              │  SeaweedFS   │
│              │             │                              │◀─────────────│  (S3 兼容)   │
└──────────────┘             │  端口: gRPC=5006, HTTP=5007  │              └──────────────┘
  SubmitMistake              │                              │
  Upload                     │              │               │
                             │              │ EF Core       │
                             │              ▼               │
┌──────────────┐             │       ┌──────────────┐       │
│              │             │       │ PostgreSQL / │       │
│teacher_portal│─────────────│       │ SQLite       │       │
│  (教师端)    │  gRPC :5006 │       └──────────────┘       │
└──────────────┘             │                               │
  GetPendingReviewUploads,   │                               │
  ReviewMistakeItem,         │                               │
  CompleteUploadReview,      │                               │
  AddMistakeItem,            │                               │
  DeleteMistakeItem,         │                               │
  GetMistakeItemsByUpload    │                               │
                             │                               │
┌──────────────┐             │                               │
│  user_portal │─────────────│                               │
│  (学生端)    │  gRPC :5006 │                               │
└──────────────┘             │                               │
  GetMistakeItemList,        │                               │
  GetMistakeItem,            │                               │
  DeleteMistakeItem          │                               │
                             └──────────────────────────────┘
```

## 上游调用方

### ruoyu.student

- **协议**: gRPC (HTTP/2)，目标端口 5006
- **调用方组件**: `GrpcMistakeSubmitter`（实现 `IMistakeSubmitter`）、`GrpcUploadDispatcher`
- **调用接口**: `SubmitMistakeUpload`
- **典型用途**: `UploadAnalysisWorker` 在 VL 图像分析完成后，自动将上传记录分发到 mistake 服务创建错题条目

### admin_portal

- **协议**: gRPC (HTTP/2)，目标端口 5006
- **调用接口**: `GetMistakeItemList`, `GetMistakeItem`, `GetMistakeItemsByUpload`, `UpdateMistakeItem`, `GetAllReferencedImagePaths`
- **典型用途**: 管理后台查看/编辑错题数据；`OssAuditWorker` 调用 `GetAllReferencedImagePaths` 进行 OSS 垃圾清理审计

### teacher_portal

- **协议**: gRPC (HTTP/2)，目标端口 5006
- **调用接口**: `GetPendingReviewUploads`, `GetMistakeItemsByUpload`, `ReviewMistakeItem`, `CompleteUploadReview`, `AddMistakeItem`, `DeleteMistakeItem`, `GetMistakeItemList`
- **典型用途**: 教师审核流程的核心消费者——查看待审核列表、逐条审核错题、完成整批审核

### user_portal

- **协议**: gRPC (HTTP/2)，目标端口 5006
- **调用接口**: `GetMistakeItemList`, `GetMistakeItem`, `DeleteMistakeItem`
- **典型用途**: 学生端查看自己的错题列表和详情，可删除自己的错题记录

## 下游依赖

### OSS (MinIO / SeaweedFS)

- **协议**: S3 兼容 API
- **接口**: `IOssService`（生产: `S3OssService`，开发: `LocalFileOssService`，由 `USE_LOCAL_OSS` 环境变量控制）
- **调用场景**: `ReviewMistakeItem` 中单条图片迁移；`CompleteUploadReview` 中批量图片迁移（uploads/ → mistakes/）
- **失败策略**: 单图迁移失败不阻塞整体流程，catch 后 log error 继续

### PostgreSQL / SQLite

- **协议**: Npgsql (PostgreSQL) / Microsoft.Data.Sqlite
- **访问方式**: EF Core
- **数据库**: `ruoyu_study_mistake`
- **选择逻辑**: 连接字符串含 `Host=` 或 `Server=` → PostgreSQL，否则 → SQLite

## 服务边界

| 边界规则 | 说明 |
|----------|------|
| 数据权威 | mistake 是 `mistake_item`、`mistake_practice`、`process_log` 三张表的唯一写入者 |
| 只读引用 | `student_id`、`source_upload_id`、`reviewer_id` 为外部引用字段，mistake 不校验其存在性 |
| 不调用其他业务服务 | 不调用 Homework、QuestionBank、Student 等其他业务微服务的 gRPC 接口 |
| 不做认证鉴权 | 认证在 portal 层完成，mistake 信任调用方传入的身份参数 |
| OSS 路径归属 | `mistakes/` 前缀由 mistake 主责；`uploads/` 前缀由 student 主责 |
