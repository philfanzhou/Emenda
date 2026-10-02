# 错题本 gRPC 服务 — 设计文档

## 架构概述

本服务采用五层分层架构，严格遵循依赖倒置原则：上层依赖下层接口，下层实现由 DI 容器注入。

## 分层说明

```
┌─────────────────────────────────────────────────┐
│  Host — ASP.NET Core gRPC 服务器入口             │
│  依赖注入配置、中间件、健康检查                    │
├─────────────────────────────────────────────────┤
│  Service — gRPC 服务实现                         │
│  MistakeServiceImpl: 请求/响应映射、输入验证、     │
│  审核后编排 (Student gRPC MigrateImagesToMistake + │
│  student gRPC回调)                                │
├─────────────────────────────────────────────────┤
│  Domain — 领域模型与业务逻辑                      │
│  MistakeDomainService, Models, 仓储接口           │
├─────────────────────────────────────────────────┤
│  Database — 数据访问层                           │
│  EF Core DbContext, 实体配置, 迁移, 仓储实现,     │
│  工作单元                                        │
├─────────────────────────────────────────────────┤
│  Contract — gRPC 契约                           │
│  mistake.proto, mistake.common.proto, 共享枚举    │
└─────────────────────────────────────────────────┘
```

## 技术栈

| 组件 | 技术 | 版本/说明 |
|------|------|-----------|
| 运行时 | .NET | 8.0 |
| 通信协议 | gRPC | ASP.NET Core gRPC, HTTP/2 |
| ORM | Entity Framework Core | 8.0, Npgsql provider |
| 数据库 | PostgreSQL | 单库 ruoyu_study_mistake |
| 对象存储 | MinIO / SeaweedFS | S3 兼容接口, IOssService 抽象 |
| 测试 | xUnit + Moq + FluentAssertions | 单元测试 + Mock |
| 端口 | gRPC=5006, HTTP=5007 | |

## 依赖关系图

```
Host ──▶ Service ──▶ Domain ──▶ Contract
  │         │          │
  │         │          ▼
  │         └──▶ Database ──▶ Domain ──▶ Contract
  │
  └──────────▶ Database ──▶ Domain ──▶ Contract

外部依赖:
  Service ──▶ StudentLearningGrpcServiceClient (MigrateImagesToMistake, RemoveImagesFromRecord, MarkUploadRecordAsReturned)
  Host ──▶ MistakeDbContext (PostgreSQL)
```

## 详细设计索引

| 功能域 | 详细设计 |
|--------|----------|
| MistakeDispatch | [SubmitMistakeUpload](../modules/SourceIntake.md) |
| MistakeReview | [ReviewMistakeItem](../modules/MistakeReview/ReviewMistakeItem/03-DESIGN.md) · [GetPendingReviewUploads](../modules/MistakeReview/GetPendingReviewUploads/03-DESIGN.md) · ~~[CompleteUploadReview](../modules/MistakeReview/CompleteUploadReview/03-DESIGN.md)~~ *(已废弃)* |
| MistakeManagement | [GetMistakeItem](../modules/MistakeManagement/GetMistakeItem/03-DESIGN.md) · [GetMistakeItemsByUpload](../modules/MistakeManagement/GetMistakeItemsByUpload/03-DESIGN.md) · [GetMistakeItemList](../modules/MistakeManagement/GetMistakeItemList/03-DESIGN.md) · [AddMistakeItem](../modules/SourceIntake.md) · [UpdateMistakeItem](../modules/MistakeManagement/UpdateMistakeItem/03-DESIGN.md) · [DeleteMistakeItem](../modules/MistakeManagement/DeleteMistakeItem/03-DESIGN.md) |
| MistakePractice | [AddMistakePractice](../modules/MistakePractice/AddMistakePractice/03-DESIGN.md) · [GetMistakePractices](../modules/MistakePractice/GetMistakePractices/03-DESIGN.md) |
| MistakeProcessLogging | [LogMistakeProcess](../modules/MistakeProcessLogging/LogMistakeProcess/03-DESIGN.md) · [GetMistakeProcessHistory](../modules/MistakeProcessLogging/GetMistakeProcessHistory/03-DESIGN.md) |
| ImagePathQuery | [GetAllReferencedImagePaths](../modules/ImagePathQuery/GetAllReferencedImagePaths/03-DESIGN.md) |
| 数据库设计 | [database/](../database/) |
| 外部集成设计 | [Integration/](../Integration/) |
