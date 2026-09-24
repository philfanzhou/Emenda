# ADR-001：Mistake 服务目标态为 WebAPI-Only（冻结决策）

## 状态

已接受（Accepted） — 2026-07-12

## 背景

ruoyu.mistake 服务当前是**纯 gRPC 服务**：

- 18 个 RPC 方法通过 `MistakeGrpcService`（proto）+ `MistakeServiceImpl` 暴露
- 端口 5006 仅 HTTP2 gRPC，HTTP 端口 5007 仅用于健康检查
- 无 JWT 认证（鉴权全靠 BFF 层）
- 4 个 portal（user/teacher/assistant/admin）通过 mistake gRPC client 调用
- mistake 服务通过 student gRPC client 反向调用 student 的 3 个 RPC（轮询 Processing 记录、迁移图片、退回创建）

此前 mistake 一直是纯 gRPC，未做协议收敛。本 ADR 冻结 WebAPI-Only 目标态。

**前置条件**：student 服务已完成 WebAPI-Only 迁移（见 `ruoyu.student/docs/overview/ADR-001-student-webapi-only.md`），student 的 Contract 项目已删除。mistake 服务当前**无法编译**——其 csproj 仍引用已删除的 `Ruoyu.Study.Student.Contract.csproj`，本 ADR 的执行将恢复编译。

## 决策

**mistake 服务目标态为 WebAPI-Only**：全部 18 个 API 通过 HTTP 暴露，gRPC 端口 5006 下线，HTTP 端口 5007 成为唯一端口。

## 约束

| ID | 约束 |
|----|------|
| C1 | **所有 18 个 RPC 1:1 迁移为 HTTP 端点**，路由按业务域分组。|
| C2 | **业务逻辑保留在 DomainService**（IMistakeDomainService / IReanalyzeJobService），HTTP 端点只做协议适配。|
| C3 | **proto 生成的 DTO 迁移为 C# record/class**，放在 Service.Dtos 中。|
| C4 | **proto 枚举（ReviewStatus / ProcessAction / ReanalyzeJobStatus）迁移为 C# 枚举**，放在 Service.Dtos 或 Domain.Models 中。|
| C5 | **mistake→student 的 3 个 gRPC 调用改为 IStudentHttpClient**（CreateReturnedRecord / GetProcessingUploadRecords / RemoveProcessedImages），需向 IStudentHttpClient 新增 3 个方法。|
| C6 | **2 个共享 BFF 库从 mistake gRPC client 改为 IMistakeHttpClient**（MistakeUploadBff.Shared / MistakeReviewBff.Shared）。|
| C7 | **4 个 portal 的 mistake gRPC client 注册改为 IMistakeHttpClient 注册**（admin/teacher/user/assistant）。|
| C8 | **不补充 JWT 认证**，鉴权仍在 BFF 层。|
| C9 | **gRPC 下线后**：`AddGrpc()` / `MapGrpcService<>()` 移除；`mistake.proto` / `mistake.common.proto` 删除；端口 5006 下线；Contract 项目删除。|
| C10 | **MistakeBff.HttpClients 库保留**：继续承载 IStudentHttpClient（student 调用）+ 新增 IMistakeHttpClient（mistake 调用），库名沿用不改名以减少波及面。|
| C11 | **UploadPollingWorker 改用 IStudentHttpClient**：原先通过 gRPC client 调用的 GetProcessingUploadRecords / RemoveProcessedImages 改为 HTTP。|
| C12 | **CompleteUploadReview 保持 `[Obsolete]`**：仅保留供 LegacyClean 运维兜底，HTTP 端点也保留。|

## 路由设计

18 个端点按 6 个业务域分组：

| 域 | 端点数 | 路由 |
|----|--------|---------|
| Mistake CRUD | 5 | `GET/POST/PUT/DELETE /api/mistakes` |
| Upload & Review | 5 | `POST /api/mistakes/upload`、`GET /api/mistakes/pending-reviews`、`POST /api/mistakes/{id}/review`、`POST /api/mistakes/complete-review` |
| Reanalyze | 3 | `POST /api/mistakes/{id}/reanalyze`、`POST /api/mistakes/reanalyze/jobs`、`GET /api/mistakes/reanalyze/active` |
| Practice | 2 | `POST/GET /api/mistakes/{id}/practices` |
| Process Log | 2 | `POST/GET /api/mistakes/{id}/process-logs` |
| OSS Proxy | 1 | `POST /api/mistakes/presigned-url` |

### 端点清单

| # | RPC 方法 | HTTP 方法 + 路由 | 说明 |
|---|---------|-----------------|------|
| 1 | SubmitMistakeUpload | `POST /api/mistakes/upload` | 学生上传图片后创建错题 |
| 2 | GetMistakeItem | `GET /api/mistakes/{id}` | 按 ID 查询单条错题 |
| 3 | GetMistakeItemsByUpload | `GET /api/mistakes/by-upload/{sourceUploadId}` | 按上传批次查询错题列表 |
| 4 | GetMistakeItemList | `GET /api/mistakes` | 分页查询错题列表（带筛选） |
| 5 | GetPendingReviewUploads | `GET /api/mistakes/pending-reviews` | 查询待审核上传批次 |
| 6 | ReviewMistakeItem | `POST /api/mistakes/{id}/review` | 审核单条错题（确认/退回） |
| 7 | ReanalyzeMistakeItem | `POST /api/mistakes/{id}/reanalyze` | 提交重新分析任务 |
| 8 | GetReanalyzeJobs | `POST /api/mistakes/reanalyze/jobs` | 按 Job IDs 查询重新分析任务 |
| 9 | GetActiveReanalyzeJobs | `GET /api/mistakes/reanalyze/active` | 查询活跃的重新分析任务 |
| 10 | AddMistakeItem | `POST /api/mistakes` | 手动添加错题条目 |
| 11 | UpdateMistakeItem | `PUT /api/mistakes/{id}` | 更新错题条目 |
| 12 | DeleteMistakeItem | `DELETE /api/mistakes/{id}` | 删除错题条目 |
| 13 | CompleteUploadReview | `POST /api/mistakes/complete-review` | ~~完成整批审核~~ `[Obsolete]` |
| 14 | AddMistakePractice | `POST /api/mistakes/{id}/practices` | 添加练习记录 |
| 15 | GetMistakePractices | `GET /api/mistakes/{id}/practices` | 查询练习记录 |
| 16 | LogMistakeProcess | `POST /api/mistakes/{id}/process-logs` | 记录处理日志 |
| 17 | GetMistakeProcessHistory | `GET /api/mistakes/{id}/process-logs` | 查询处理历史 |
| 18 | GetPresignedUrl | `POST /api/mistakes/presigned-url` | 生成 mistakes/ 路径图片的预签名 URL |

## IStudentHttpClient 新增方法

mistake 服务原先通过 student gRPC client 调用的 3 个方法，需新增到 IStudentHttpClient：

| gRPC 方法 | 新增 HTTP 方法 | 路由 | 说明 |
|-----------|---------------|------|------|
| CreateReturnedRecord | `CreateReturnedRecordAsync` | `POST /api/students/{studentId}/uploads/returned` | 审核退回时创建 Returned 上传记录 |
| GetProcessingUploadRecords | `GetProcessingUploadRecordsAsync` | `GET /api/uploads/processing` | 轮询 Processing 状态的上传记录 |
| RemoveProcessedImages | `RemoveProcessedImagesAsync` | `POST /api/uploads/{recordId}/remove-processed` | 通知 student 移除已处理的图片 |

> 注：student 服务的 3 个 HTTP 端点已全部就绪，mistake 侧只需新增 client 方法。

## 影响范围

| 组件 | 改动 |
|------|------|
| mistake 服务（Host + Service + Contract） | 删除 gRPC 层，新建 HTTP 端点层，删除 Contract 项目，Program.cs 移除 gRPC/Kestrel HTTP2 |
| mistake 服务 UploadPollingWorker | gRPC client → IStudentHttpClient（2 个调用点）|
| mistake 服务 MistakeServiceImpl | gRPC client → IStudentHttpClient（1 个调用点：CreateReturnedRecord）|
| IStudentHttpClient（MistakeBff.HttpClients） | 新增 3 个方法 + 实现 |
| MistakeBff.HttpClients 库 | 新增 IMistakeHttpClient 接口 + 实现，删除 mistake.proto Protobuf 引用 |
| MistakeUploadBff.Shared | mistake gRPC client → IMistakeHttpClient |
| MistakeReviewBff.Shared | mistake gRPC client → IMistakeHttpClient |
| user_portal | mistake gRPC client → IMistakeHttpClient |
| teacher_portal | mistake gRPC client → IMistakeHttpClient |
| assistant_portal | mistake gRPC client → IMistakeHttpClient |
| Ruoyu.Admin | mistake gRPC client → IMistakeHttpClient |

## 迁移策略

分阶段提交：
1. **Phase 1**：mistake 服务端 gRPC→WebAPI（含 HTTP 端点创建 + IStudentHttpClient 新增 3 方法 + gRPC 删除 + Contract 删除）
2. **Phase 2**：2 个共享 BFF 库 + 4 个 portal 改造为 IMistakeHttpClient
3. **Phase 3**：测试修复 + 全面编译验证 + 提交 push

## 失败语义保持

原有 gRPC 的失败语义在 HTTP 端点中保持一致：

| 场景 | gRPC 行为 | HTTP 行为 |
|------|----------|----------|
| 参数校验失败 | `RpcException(InvalidArgument)` | `400 BadRequest` + `{ success: false, message }` |
| 资源不存在 | `RpcException(NotFound)` | `404 NotFound` + `{ success: false, message }` |
| 业务校验失败 | `RpcException(InvalidArgument)` | `400 BadRequest` + `{ success: false, message }` |
| 系统异常 | `RpcException(Internal)` | `500 InternalServerError` + `{ success: false, message }` |

> IMistakeHttpClient 实现内部捕获 `HttpRequestException` 并返回空/失败 DTO（与 IStudentHttpClient 保持一致的降级策略），调用方无需处理异常。
