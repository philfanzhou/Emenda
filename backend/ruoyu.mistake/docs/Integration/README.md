# Integration — 集成索引

本目录包含 `ruoyu.mistake` 模块与外部系统/模块的集成文档，按功能块组织。

## 目录结构

```
Integration/
├── README.md              (本文件，索引)
├── Student/               (与 ruoyu.student 的集成)
│   ├── 01-FEATURE.md
│   ├── 02-SPEC.md
│   ├── 03-DESIGN.md
│   ├── 04-TASKS.md
│   ├── 05-TESTS.md
│   └── 06-CONVENTIONS.md
├── Oss/                   (与 OSS/SeaweedFS 的集成)
│   ├── 01-FEATURE.md
│   ├── 02-SPEC.md
│   ├── 03-DESIGN.md
│   ├── 04-TASKS.md
│   ├── 05-TESTS.md
│   └── 06-CONVENTIONS.md
├── Portals/               (与各 Portal 的集成)
│   ├── 01-FEATURE.md
│   ├── 02-SPEC.md
│   ├── 03-DESIGN.md
│   ├── 04-TASKS.md
│   ├── 05-TESTS.md
│   └── 06-CONVENTIONS.md
└── Storage/               (数据库与存储层集成)
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 各功能块概述

### Student 集成

mistake 与 `ruoyu.student` 模块之间的单向 gRPC 调用和数据引用。

- **student → mistake**：通过 `UploadAnalysisWorker` 和 `GrpcUploadDispatcher` 调用 `SubmitMistakeUpload`，提交错题上传。
- **mistake → student**：mistake 不调用 student 的任何 gRPC 接口，仅通过 `student_id` 字段只读引用学生数据，不校验学生是否存在。

→ 详见 [Student/](Student/)

### Oss 集成

mistake 通过 `IOssService` 接口与对象存储交互，完成上传审核后的文件迁移。

- **核心流程**：`CompleteUploadReview` 中将文件从 `uploads/` 复制到 `mistakes/`，并删除 `uploads/` 原文件。
- **实现**：支持 `S3OssService`（MinIO / SeaweedFS）和 `LocalFileOssService`（本地文件系统）两种实现。
- **关键配置**：`Oss:Endpoint`、`Oss:AccessKey`、`Oss:SecretKey`、`Oss:BucketName`、`USE_LOCAL_OSS`、`OSS_LOCAL_PATH`

→ 详见 [Oss/](Oss/)

### Portals 集成

mistake 为三个 Portal 提供业务接口，权限校验在 Portal 层完成，mistake 本身不做权限校验。

| Portal | 主要接口 |
|--------|---------|
| **admin_portal** | `GetMistakeItemList`、`GetMistakeItem`、`GetMistakeItemsByUpload`、`UpdateMistakeItem`、`GetAllReferencedImagePaths` |
| **teacher_portal** | `GetPendingReviewUploads`、`GetMistakeItemsByUpload`、`ReviewMistakeItem`、`GetMistakeItem`、`DeleteMistakeItem`、`AddMistakeItem`、`CompleteUploadReview`、`GetMistakeItemList` |
| **user_portal** | `GetMistakeItemList`、`GetMistakeItem`、`DeleteMistakeItem` |

→ 详见 [Portals/](Portals/)

### Storage 集成

mistake 的数据库与存储层配置。

- **数据库**：PostgreSQL（生产）/ SQLite（开发），数据库名 `ruoyu_study_mistake`。
- **ORM**：EF Core + Npgsql。
- **初始化**：`DatabaseInitializer` 处理旧表迁移和数据库初始化。
- **关键配置**：`ConnectionStrings:Default`

→ 详见 [Storage/](Storage/)

## 顶层文档

| 文档 | 说明 |
|------|------|
| [SystemContext.md](../SystemContext.md) | 系统上下文与整体架构 |
| [Integration.md](../Integration.md) | 集成关系总览 |
| [KeyFlows.md](../KeyFlows.md) | 关键业务流程 |
| [DataOwnership.md](../DataOwnership.md) | 数据所有权与职责划分 |
| [Deployment.md](../Deployment.md) | 部署与运维 |
| [Troubleshooting.md](../Troubleshooting.md) | 故障排查指南 |
