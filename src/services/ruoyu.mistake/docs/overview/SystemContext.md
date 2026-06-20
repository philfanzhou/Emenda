# ruoyu.mistake 系统上下文

ruoyu.mistake 是 Ruoyu.Study 教育平台的错题本 gRPC 微服务，负责错题记录的全生命周期管理——轮询采集学生上传的错题图片、VL 视觉语言分析、自动创建待审核错题、教师逐条审核确认/驳回、审核完成后触发 OSS 图片迁移，到错题练习追踪与类型分析。本服务是错题业务域的唯一数据权威，通过轮询 ruoyu.student 获取已分类的上传记录，并通过 gRPC 对外暴露能力。

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
┌──────────────┐  轮询 gRPC  ┌──────────────────────────────┐     S3 API    ┌──────────────┐
│              │◀────────────│                              │─────────────▶│              │
│  ruoyu.      │  :5005      │       ruoyu.mistake          │              │  MinIO /     │
│  student     │             │   (错题本 gRPC 微服务)        │              │  SeaweedFS   │
│              │             │                              │◀─────────────│  (S3 兼容)   │
└──────────────┘             │  端口: gRPC=5006, HTTP=5007  │              └──────────────┘
  GetProcessing              │                              │        ┌──────────────┐
  UploadRecords              │              │               │        │  VL Provider │
  RemoveProcessed            │              │ EF Core       │        │ (SiliconFlow │
  Images                     │              ▼               │        │  Qwen3-VL)   │
  CreateReturned             │       ┌──────────────┐       │        └──────────────┘
  Record                     │       │ PostgreSQL / │       │              ▲
                             │       │ SQLite       │       │              │ HTTP
┌──────────────┐             │       └──────────────┘       │              │
│              │             │                               │              │
│teacher_portal│─────────────│                               │──────────────┘
│  (教师端)    │  gRPC :5006 │                               │  VL 图片分析
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

### ruoyu.student（被动查询）

- **协议**: gRPC (HTTP/2)，目标端口 5005
- **调用方组件**: `UploadPollingWorker`（Mistake 服务内的 BackgroundService）
- **调用接口**: `GetProcessingUploadRecords`（轮询）、`RemoveProcessedImages`（处理完成通知）、`CreateReturnedUploadRecord`（退回）
- **典型用途**: Mistake 每 60 秒轮询 Student 的 Processing 状态上传记录，获取标记为 "mistake" 的图片进行 VL 分析和错题创建

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

### ruoyu.student（主动调用）

- **协议**: gRPC (HTTP/2)，目标端口 5005
- **调用接口**: `GetProcessingUploadRecords`（轮询采集）、`RemoveProcessedImages`（处理完成通知）、`CreateReturnedUploadRecord`（退回创建记录）
- **调用场景**: `UploadPollingWorker` 轮询 Processing 记录；审核驳回时创建 Returned 记录

### VL Provider (SiliconFlow Qwen3-VL)

- **协议**: HTTP/HTTPS，OpenAI 兼容 API
- **接口**: `{ApiBaseUrl}/chat/completions`
- **调用场景**: `VlUploadAnalyzer` 对错题图片进行视觉语言分析，按学科/年级分组
- **配置**: `VlAnalyzer` 配置段（ApiKey、ApiBaseUrl、Model）
- **失败策略**: 分析失败时记录错误，跳过该记录，下轮重试

### OSS (MinIO / SeaweedFS)

- **协议**: S3 兼容 API
- **接口**: `IOssService`（生产: `S3OssService`，开发: `LocalFileOssService`）
- **允许前缀**: `mistakes/`、`uploads/`（轮询期间需要读取 uploads/ 下的图片）
- **调用场景**: `UploadPollingWorker` 中图片迁移（uploads/ → mistakes/）；`ReviewMistakeItem` 驳回时反向迁移（mistakes/ → uploads/）；缩略图生成
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
| 主动调用 Student | 通过 `UploadPollingWorker` 轮询 Student 的 Processing 记录；审核驳回时调用 `CreateReturnedUploadRecord` |
| 不调用其他业务服务 | 不调用 Homework、QuestionBank 等其他业务微服务 |
| 不做认证鉴权 | 认证在 portal 层完成，mistake 信任调用方传入的身份参数 |
| OSS 路径归属 | `mistakes/` 前缀由 mistake 主责；`uploads/` 前缀由 student 主责；轮询期间 mistake 可读写两个前缀 |
