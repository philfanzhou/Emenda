# ruoyu.mistake 系统上下文文档

## 服务概述

| 属性 | 值 |
|------|------|
| 服务名称 | ruoyu.mistake |
| 定位 | 错题本 gRPC 微服务 |
| 核心功能 | 错题记录管理、审核流程、练习追踪、错题分析 |
| 运行时 | .NET 8.0 |
| 框架 | ASP.NET Core gRPC |
| 端口 | gRPC: 5007 (HTTP/2), HTTP: 5007 (HTTP/1) |
| Proto 包名 | `ruoyu.study.mistake.v1` |
| gRPC 服务名 | `MistakeGrpcService` |

## 系统上下文图

```
                              ┌─────────────────────────┐
                              │    QuantumZhou.Identity  │
                              │     (无直接集成)         │
                              └─────────────────────────┘

┌──────────────┐    gRPC      ┌───────────────────────────────────────────┐
│              │──────────────▶│                                           │
│  ruoyu.      │  :5006       │                                           │
│  student     │◀──────────────│              ruoyu.mistake                │
│              │  gRPC :5005   │                                           │
└──────────────┘              │         (错题本 gRPC 微服务)               │
                              │                                           │
┌──────────────┐    gRPC      │  端口: gRPC 5007 / HTTP 5007             │
│              │──────────────▶│                                           │
│ admin_portal │  :5006       │                                           │
│              │              │                  │                        │
└──────────────┘              │                  │ IOssService           │
                              │                  ▼                        │
┌──────────────┐    gRPC      │         ┌──────────────┐                 │
│              │──────────────▶│         │ MinIO /      │                 │
│teacher_portal│  :5006       │         │ SeaweedFS    │                 │
│              │              │         │ (S3 兼容)    │                 │
└──────────────┘              │         └──────────────┘                 │
                              │                                           │
┌──────────────┐    gRPC      │                  │ EF Core               │
│              │──────────────▶│                  ▼                        │
│ user_portal  │  :5006       │         ┌──────────────┐                 │
│              │              │         │ PostgreSQL / │                 │
└──────────────┘              │         │ SQLite       │                 │
                              │         └──────────────┘                 │
┌──────────────┐              │                                           │
│              │              └───────────────────────────────────────────┘
│ruoyu.homework│              ▲
│ (无直接集成) │              │
└──────────────┘     student 的 GrpcUploadDispatcher
                      同时持有 homework 和 mistake
                      客户端，但 homework 与 mistake
                      之间无直接调用
```

## 上游调用方（谁调用 mistake）

### 1. ruoyu.student 服务

| 项目 | 说明 |
|------|------|
| 协议 | gRPC (HTTP/2) |
| 目标端口 | 5006 |
| 调用方式 | `GrpcMistakeSubmitter` (实现 `IMistakeSubmitter`)、`GrpcUploadDispatcher` |

**调用的 RPC 方法：**

| RPC 方法 | 调用方 | 代码位置 |
|----------|--------|----------|
| `SubmitMistakeUpload` | `GrpcMistakeSubmitter` | `backend/ruoyu.student/src/Host/Services/VlImageAnalysis/IMistakeSubmitter.cs` |
| `SubmitMistakeUpload` | `GrpcUploadDispatcher.DispatchToMistakeAsync` | `backend/ruoyu.student/src/Host/Services/VlImageAnalysis/GrpcUploadDispatcher.cs` |

**业务场景：**
- `UploadAnalysisWorker` 在 VL 图像分析完成后，自动通过 `IMistakeSubmitter` 创建错题记录
- `GrpcUploadDispatcher` 负责将上传记录分发到 mistake 服务

---

### 2. admin_portal

| 项目 | 说明 |
|------|------|
| 协议 | gRPC (HTTP/2) |
| 目标端口 | 5006 |
| 客户端 | `MistakeGrpcServiceClient` |
| 配置键 | `MistakeGrpcService:Address` = `http://localhost:5006` |

**调用的 RPC 方法：**

| RPC 方法 | 代码位置 |
|----------|----------|
| `GetMistakeItemList` | `admin_portal/backend/Controllers/MistakeController.cs` |
| `GetMistakeItem` | `admin_portal/backend/Controllers/MistakeController.cs` |
| `GetMistakeItemsByUpload` | `admin_portal/backend/Controllers/MistakeController.cs` |
| `UpdateMistakeItem` | `admin_portal/backend/Controllers/MistakeController.cs` |
| `MigrateImages` | `admin_portal/backend/Controllers/MistakeController.cs` |
| `GetAllReferencedImagePaths` | `admin_portal/backend/Services/OssAuditWorker.cs` |

**业务场景：**
- 管理后台查看/编辑错题数据
- `OssAuditWorker` 调用 `GetAllReferencedImagePaths` 获取所有被引用的图片路径，用于 OSS 垃圾清理审计

---

### 3. teacher_portal

| 项目 | 说明 |
|------|------|
| 协议 | gRPC (HTTP/2) |
| 目标端口 | 5006 |
| 客户端 | `MistakeGrpcServiceClient` |
| 配置键 | `MistakeService:GrpcUrl` = `http://localhost:5006` |

**调用的 RPC 方法：**

| RPC 方法 | 代码位置 |
|----------|----------|
| `GetPendingReviewUploads` | `teacher_portal/backend/Controllers/MistakeController.cs` |
| `GetMistakeItemsByUpload` | `teacher_portal/backend/Controllers/MistakeController.cs` |
| `ReviewMistakeItem` | `teacher_portal/backend/Controllers/MistakeController.cs` |
| `GetMistakeItem` | `teacher_portal/backend/Controllers/MistakeController.cs` |
| `DeleteMistakeItem` | `teacher_portal/backend/Controllers/MistakeController.cs` |
| `AddMistakeItem` | `teacher_portal/backend/Controllers/MistakeController.cs` |
| `CompleteUploadReview` | `teacher_portal/backend/Controllers/MistakeController.cs` |
| `GetMistakeItemList` | `teacher_portal/backend/Controllers/MistakeController.cs` |

**业务场景：**
- 教师审核流程的核心消费者
- 教师查看待审核上传 → 逐条审核错题 → 完成整批审核

---

### 4. user_portal

| 项目 | 说明 |
|------|------|
| 协议 | gRPC (HTTP/2) |
| 目标端口 | 5006 |
| 客户端 | `MistakeGrpcServiceClient` |
| 配置键 | `MistakeService:GrpcUrl` = `http://localhost:5006` |

**调用的 RPC 方法：**

| RPC 方法 | 代码位置 |
|----------|----------|
| `GetMistakeItemList` | `user_portal/backend/Controllers/MistakeController.cs` |
| `GetMistakeItem` | `user_portal/backend/Controllers/MistakeController.cs` |
| `DeleteMistakeItem` | `user_portal/backend/Controllers/MistakeController.cs` |

**业务场景：**
- 学生端查看自己的错题列表和详情
- 学生可删除自己的错题记录

---

## 下游依赖（mistake 调用谁）

### 1. ruoyu.student 服务

| 项目 | 说明 |
|------|------|
| 协议 | gRPC (HTTP/2) |
| 目标端口 | 5005 |
| 客户端 | `StudentLearningGrpcService.StudentLearningGrpcServiceClient` |
| 配置键 | `StudentGrpcService:Address` = `http://localhost:5005` |

**调用的 RPC 方法：**

| RPC 方法 | 调用场景 | 代码位置 |
|----------|----------|----------|
| `RemoveImagesFromRecord` | `CompleteUploadReview` 审核完成后，从上传记录中移除已处理的图片 | `MistakeServiceImpl.cs` L344-351 |

**代码依据（Program.cs）：**
```csharp
var studentGrpcAddress = builder.Configuration["StudentGrpcService:Address"] ?? "http://localhost:5005";
builder.Services.AddGrpcClient<StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient>(options =>
{
    options.Address = new Uri(studentGrpcAddress);
});
```

---

### 2. OSS / SeaweedFS (MinIO)

| 项目 | 说明 |
|------|------|
| 协议 | S3 兼容 API |
| 接口 | `IOssService` |
| 实现 | `S3OssService`（生产）/ `LocalFileOssService`（开发） |

**配置项：**

| 配置键 | 说明 | 示例值 |
|--------|------|--------|
| `Oss:Endpoint` | S3 端点 | `localhost:8333` |
| `Oss:AccessKey` | 访问密钥 | `mock_access_key` |
| `Oss:SecretKey` | 密钥 | `mock_secret_key` |
| `Oss:BucketName` | 存储桶名 | `ruoyu-study` |

**环境变量：**

| 变量名 | 说明 |
|--------|------|
| `USE_LOCAL_OSS=1` | 使用本地文件系统替代 S3 |
| `OSS_LOCAL_PATH` | 本地 OSS 存储路径，默认 `data/oss` |

**业务场景：**
- `CompleteUploadReview` 中：将图片从 `uploads/` 前缀复制到 `mistakes/` 前缀，然后删除 `uploads/` 下的原图

**代码依据（Program.cs L54-68）：**
```csharp
var useLocalOss = Environment.GetEnvironmentVariable("USE_LOCAL_OSS") == "1";
if (useLocalOss)
{
    var localPath = Environment.GetEnvironmentVariable("OSS_LOCAL_PATH") ?? "data/oss";
    builder.Services.AddSingleton<IOssService>(new LocalFileOssService(localPath));
}
else
{
    builder.Services.Configure<OssOptions>(builder.Configuration.GetSection("Oss"));
    builder.Services.AddScoped<IOssService, S3OssService>(...);
}
```

---

### 3. PostgreSQL / SQLite

| 项目 | 说明 |
|------|------|
| 协议 | Npgsql (PostgreSQL) / Microsoft.Data.Sqlite |
| 访问方式 | EF Core |
| 数据库名 | `ruoyu_study_mistake` |
| 配置键 | `ConnectionStrings:Default` |

**连接字符串示例：**
```
Host=localhost;Port=5432;Database=ruoyu_study_mistake;Username=phil
```

**数据库选择逻辑（Program.cs L43-52）：**
- 连接字符串包含 `Host=` 或 `Server=` → 使用 PostgreSQL (`UseNpgsql`)
- 否则 → 使用 SQLite (`UseSqlite`)，默认路径 `data/sqlite/ruoyu_study_mistake.db`

---

## 无直接集成的服务

### ruoyu.homework

- homework 与 mistake 之间无直接 gRPC 调用
- student 服务的 `GrpcUploadDispatcher` 同时持有 homework 和 mistake 的 gRPC 客户端，用于将上传记录分发到对应服务
- 这是 student 服务层面的编排逻辑，不构成 homework 与 mistake 之间的直接集成

### QuantumZhou.Identity

- mistake 服务本身不做权限校验
- 认证/鉴权在各 portal 层（admin_portal、teacher_portal、user_portal）完成
- portal 层在调用 mistake gRPC 接口前已完成身份验证和权限检查

---

## gRPC 接口清单

以下是 `MistakeGrpcService` 定义的全部 RPC 方法（定义于 `mistake.proto`）：

| RPC 方法 | 请求类型 | 响应类型 | 主要调用方 |
|----------|----------|----------|------------|
| `SubmitMistakeUpload` | `SubmitMistakeUploadRequest` | `SubmitMistakeUploadResponse` | ruoyu.student |
| `GetMistakeItem` | `IdRequest` | `MistakeItemDto` | admin_portal, teacher_portal, user_portal |
| `GetMistakeItemsByUpload` | `GetMistakeItemsByUploadRequest` | `MistakeItemListResponse` | admin_portal, teacher_portal |
| `GetMistakeItemList` | `GetMistakeItemListRequest` | `MistakeItemPageResult` | admin_portal, teacher_portal, user_portal |
| `GetPendingReviewUploads` | `GetPendingReviewUploadsRequest` | `PendingReviewUploadListResponse` | teacher_portal |
| `ReviewMistakeItem` | `ReviewMistakeItemRequest` | `BoolResponse` | teacher_portal |
| `AddMistakeItem` | `AddMistakeItemRequest` | `MistakeItemDto` | teacher_portal |
| `UpdateMistakeItem` | `UpdateMistakeItemRequest` | `MistakeItemDto` | admin_portal |
| `DeleteMistakeItem` | `IdRequest` | `BoolResponse` | teacher_portal, user_portal |
| `CompleteUploadReview` | `CompleteUploadReviewRequest` | `CompleteUploadReviewResponse` | teacher_portal |
| `AddMistakePractice` | `AddMistakePracticeRequest` | `MistakePracticeDto` | — |
| `GetMistakePractices` | `IdRequest` | `MistakePracticeListResponse` | — |
| `AnalyzeMistakeType` | `AnalyzeMistakeRequest` | `MistakeTypeResponse` | — |
| `LogMistakeProcess` | `LogMistakeProcessRequest` | `BoolResponse` | — |
| `GetMistakeProcessHistory` | `IdRequest` | `ProcessLogListResponse` | — |
| `GetAllReferencedImagePaths` | `Empty` | `GetAllReferencedImagePathsResponse` | admin_portal |

> 注：标记"—"的 RPC 方法目前无已知的外部 portal 调用方，可能由内部功能或未来功能使用。

---

## 基础设施依赖汇总

| 依赖 | 用途 | 生产环境 | 开发环境 |
|------|------|----------|----------|
| 数据库 | 持久化存储 | PostgreSQL | SQLite |
| 对象存储 | 图片文件管理 | MinIO/SeaweedFS (S3) | 本地文件系统 |
| gRPC 通信 | 服务间调用 | HTTP/2 | HTTP/2 |
