# ruoyu.mistake 错误排查方案

本文档基于代码事实编写，用于联调排障时快速定位问题。

---

## 1. gRPC 调用失败

### 1.1 student → mistake：SubmitMistakeUpload 超时

**典型症状**：student 服务的 `UploadAnalysisWorker` 处理上传时，调用 mistake 的 `SubmitMistakeUpload` 无响应或报超时。

**常见日志特征**：
- student 侧：`OperationCanceledException`（`InnerException` 为 `TimeoutException`），由 `CancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30))` 触发
- mistake 侧：无对应请求日志，或请求处理耗时超过 30s

**可能根因**：
- mistake 服务未启动或端口不可达
- mistake 服务数据库连接阻塞（如 PostgreSQL 连接池耗尽）
- mistake 服务内部 `SaveChangesAsync` 慢查询

**排查步骤**：
1. 确认 mistake 服务进程存活：`curl http://<mistake-host>:5007/` 应返回 `Mistake gRPC host is running.`
2. 检查 `Endpoints:Grpc` 配置是否与 student 侧 `MistakeGrpcService:Address` 一致
3. 查看 mistake 侧日志是否有数据库异常
4. 检查网络连通性：`telnet <mistake-host> 5006`

**临时绕过**：增大 student 侧 `GrpcMistakeSubmitter` 的超时时间（修改 `IMistakeSubmitter.cs` L49 的 `CancelAfter` 值）

**根治建议**：排查 mistake 服务慢根因（通常是数据库或 OSS），确保 `SubmitMistakeUpload` 在 30s 内完成。

### 1.2 portal → mistake：StatusCode.Unavailable

**典型症状**：portal 调用 mistake 各接口返回 gRPC `StatusCode.Unavailable`。

**常见日志特征**：
- portal 侧：`RpcException: Status(StatusCode="Unavailable", Detail="...")`

**可能根因**：
- mistake 服务未启动
- gRPC 端口（默认 5006）被占用或防火墙拦截
- HTTP/2 协议协商失败（portal 用了 HTTP/1.1 端口连 gRPC）

**排查步骤**：
1. 确认 mistake 服务监听端口：检查 `Endpoints:Grpc` 配置，默认 5006 为 HTTP/2，5007 为 HTTP/1.1
2. 用 gRPC 工具直连测试：`grpcurl -plaintext <host>:5006 list`
3. 检查 Kestrel 协议配置：`Program.cs` L19-28 中 Grpc 端口必须为 `Http2`

**临时绕过**：无

**根治建议**：确保 portal 连接的是 mistake 的 gRPC 端口（HTTP/2），而非 HTTP 端口。

---

## 2. 参数校验失败

### 2.1 SubmitMistakeUpload 参数校验

**典型症状**：调用 `SubmitMistakeUpload` 返回 `StatusCode.InvalidArgument`。

**常见日志特征**：
- `"缺少学生ID"` — `student_id` 为空（`MistakeServiceImpl.cs` L36）
- `"学科值无效（有效范围：1-9）"` — `subject` 不在 1-9（L39）
- `"年级值无效（有效范围：1-12）"` — `grade` 不在 1-12（L42）
- `"缺少上传记录ID"` — `source_upload_id` 为空（L45）
- `"至少需要提供一张图片"` — `image_paths` 为空（L48）

**可能根因**：调用方传参错误，或字段映射不一致

**排查步骤**：
1. 检查 gRPC 请求体中各字段值
2. 注意 `subject` 范围是 **1-9**（SubmitMistakeUpload），与 `AddMistakeItem` 的 **0-9** 不同
3. 注意 `grade` 范围是 **1-12**（SubmitMistakeUpload），与 `AddMistakeItem` 的 **0-12** 不同

**临时绕过**：修正调用方参数

**根治建议**：统一各接口的参数范围约定，或在 proto 中增加校验注解。

### 2.2 AddMistakeItem 参数校验

**典型症状**：调用 `AddMistakeItem` 返回 `StatusCode.InvalidArgument`。

**常见日志特征**：
- `"缺少学生ID"` — `student_id` 为空（`MistakeDomainService.cs` L355-356）
- `"学科值无效（有效范围：0-9）"` — `subject` 不在 0-9（L358-359）
- `"年级值无效（有效范围：0-12）"` — `grade` 不在 0-12（L361-362）
- `"缺少上传记录ID"` — `source_upload_id` 为空（L364-365）
- `"至少需要一个图片区域"` — `source_regions` 为空（L367-368）

**可能根因**：与 `SubmitMistakeUpload` 的范围不同（0 允许值），调用方混淆了两个接口

**排查步骤**：
1. 确认调用的是哪个接口，对照对应范围
2. `AddMistakeItem` 允许 `subject=0` 和 `grade=0`（未分类场景）

**临时绕过**：修正调用方参数

**根治建议**：在文档或 proto 注释中明确两个接口的范围差异。

### 2.3 ReviewMistakeItem 参数校验

**典型症状**：调用 `ReviewMistakeItem` 返回 `StatusCode.InvalidArgument`。

**常见日志特征**：
- `"审核结果只能是确认或退回"` — `review_status` 不是 `Confirmed` 或 `Rejected`（`MistakeServiceImpl.cs` L146）
- `"只能审核待审核状态的错题"` — 错题当前不是 `PendingReview` 状态（`MistakeDomainService.cs` L111）

**可能根因**：
- 重复审核（已审核过的错题再次提交审核）
- 传入了非法的 `review_status` 枚举值

**排查步骤**：
1. 先调用 `GetMistakeItem` 查看当前 `review_status`
2. 确认 `review_status` 传入的是 `Confirmed`(2) 或 `Rejected`(3)

**临时绕过**：无

**根治建议**：前端在审核后禁用审核按钮，避免重复提交。

### 2.4 AddMistakePractice 参数校验

**典型症状**：调用 `AddMistakePractice` 返回 `StatusCode.InvalidArgument`。

**常见日志特征**：
- `"错题条目不存在"` — `mistake_item_id` 对应的错题不存在（`MistakeDomainService.cs` L223）
- `"只能对已确认的错题进行练习"` — 错题不是 `Confirmed` 状态（L227）

**可能根因**：
- 错题已被删除
- 错题仍处于 `PendingReview` 或已被 `Rejected`

**排查步骤**：
1. 调用 `GetMistakeItem` 确认错题存在且 `review_status == Confirmed`
2. 检查 `mistake_item_id` 格式是否为有效 GUID

**临时绕过**：无

**根治建议**：前端在练习入口处检查错题状态，非 Confirmed 状态不显示练习按钮。

### 2.5 CompleteUploadReview 业务校验

> **注意**: CompleteUploadReview 已废弃，仅保留供 LegacyClean 运维兜底。正常流程中不再使用此接口。

**典型症状**：调用 `CompleteUploadReview` 返回 `Success=false`。

**常见日志特征**：
- `"还有 N 条错题待审核，请先完成所有审核"` — 仍有 `PendingReview` 的错题（`MistakeDomainService.cs` L195）
- `"未找到该上传记录的错题"` — 该 `source_upload_id` 下无任何错题（L206）

**可能根因**：
- 审核流程未走完就调用了完成接口
- `source_upload_id` 拼写错误或对应错题已被删除

**排查步骤**：
1. 调用 `GetMistakeItemsByUpload(source_upload_id)` 查看该上传下所有错题状态
2. 确认所有错题均为 `Confirmed` 或 `Rejected`（不能有 `PendingReview`）

**临时绕过**：先逐一审核所有 PendingReview 的错题，再调用 CompleteUploadReview

**根治建议**：前端在"完成审核"按钮处做前置检查，提示未审核数量。

---

## 3. source_upload_id 相关问题

### 3.1 同一 source_upload_id 重复提交

**典型症状**：同一 `source_upload_id` 多次调用 `SubmitMistakeUpload`，每次都成功创建新错题，导致同一上传产生多组重复错题。

**常见日志特征**：无报错，但 `GetMistakeItemsByUpload` 返回数量异常多

**可能根因**：代码不做 `source_upload_id` 唯一性校验（`SubmitMistakeUploadAsync` 和 `AddMistakeItemAsync` 均无去重逻辑）

**排查步骤**：
1. 查询 `SELECT source_upload_id, COUNT(*) FROM mistake_item GROUP BY source_upload_id HAVING COUNT(*) > 1` 检查重复
2. 检查 student 侧是否对同一上传重复触发了 `SubmitMistakeUpload`

**临时绕过**：调用前先 `GetMistakeItemsByUpload` 检查是否已提交

**根治建议**：在 `SubmitMistakeUploadAsync` 中增加幂等校验，或在数据库层加唯一约束。

### 3.2 CompleteUploadReview 依赖 source_upload_id 查找关联 item

> **注意**: CompleteUploadReview 已废弃，仅保留供 LegacyClean 运维兜底。

**典型症状**：`CompleteUploadReview` 返回 `"未找到该上传记录的错题"`，但错题实际存在。

**常见日志特征**：`"未找到该上传记录的错题"`（`MistakeDomainService.cs` L206）

**可能根因**：
- `source_upload_id` 大小写不一致（数据库中存储的与传入的不匹配）
- 错题的 `source_upload_id` 为空字符串（旧数据迁移时默认值）

**排查步骤**：
1. 直接查询数据库：`SELECT DISTINCT source_upload_id FROM mistake_item` 确认实际存储值
2. 检查是否有 `source_upload_id = ''` 的记录

**临时绕过**：用数据库中实际存储的 `source_upload_id` 值调用

**根治建议**：在 `AddMistakeItem` / `SubmitMistakeUpload` 中对 `source_upload_id` 做规范化处理（如 trim、统一大小写）。

---

## 4. CompleteUploadReview 中 OSS 迁移失败

> **注意**: CompleteUploadReview 已废弃（标记 `[Obsolete]`），仅保留供 LegacyClean 运维兜底。正常审核流程中，图片迁移由 ReviewMistakeItem 逐条审核时即时处理。

**典型症状**：`CompleteUploadReview` 返回 `Success=true`，但部分图片未从 `uploads/` 迁移到 `mistakes/`，`source_regions` 中路径仍为 `uploads/` 前缀。

**常见日志特征**：
- `"Error migrating image {Path}"`（`MistakeServiceImpl.cs` L328）
- 正常迁移日志：`"Migrated image from {OldPath} to {NewPath}"`（L322）

**可能根因**：
- OSS 服务不可用（S3 连接超时或拒绝）
- 源文件不存在（`uploads/xxx` 已被删除或从未上传成功）
- 目标路径冲突（`mistakes/xxx` 已存在）
- `USE_LOCAL_OSS=1` 但本地路径不可写

**排查步骤**：
1. 确认 OSS 服务可用：检查 S3 endpoint 或本地文件系统权限
2. 查看日志中 `{Path}` 的具体值，确认源路径是否以 `uploads/` 开头
3. 手动验证：用 OSS 工具检查源文件是否存在
4. 检查 `Oss` 配置节（`InternalEndpoint`/`InternalSecure`/`AccessKey`/`SecretKey`/`BucketName`）是否正确

**临时绕过**：单个图片迁移失败不阻塞整体流程（代码 L325-330 catch 后继续），可手动补迁移

**根治建议**：
1. 增加 OSS 迁移重试机制
2. 增加迁移失败的补偿任务，定期扫描 `source_regions` 中仍以 `uploads/` 开头的路径并重试迁移

---

## 5. 数据库连接失败/初始化失败

### 5.1 PostgreSQL 连接失败

**典型症状**：服务启动时抛出 `NpgsqlException`，或运行时数据库操作超时。

**常见日志特征**：
- `Npgsql.NpgsqlException: Exception while connecting`
- `Npgsql.PostgresException: 3D000: database "ruoyu_study_mistake" does not exist`
- 连接池耗尽：`Timeout expired waiting for connection`

**可能根因**：
- `ConnectionStrings:Default` 配置错误（Host/Port/Username/Password）
- PostgreSQL 服务未启动
- 数据库 `ruoyu_study_mistake` 未创建
- 连接池配置不足

**排查步骤**：
1. 确认 PostgreSQL 服务运行：`pg_isready -h <host> -p 5432`
2. 确认数据库存在：`psql -l | grep ruoyu_study_mistake`
3. 测试连接字符串：`psql "Host=localhost;Port=5432;Database=ruoyu_study_mistake;Username=phil"`
4. 检查 `appsettings.json` 中 `ConnectionStrings:Default` 配置

**临时绕过**：无（服务固定使用 PostgreSQL，必须确保 PostgreSQL 可用）

**根治建议**：确保 PostgreSQL 服务稳定运行，配置正确的连接字符串。

### 5.3 结构或迁移历史不匹配

**症状**：启动失败，错误以 `Mistake database incompatible:` 开头并指出缺少或多出的关系、列、默认值、索引、约束或迁移历史。`public.mistake` 旧关系、空库、只有部分表以及列类型或历史不一致均不允许普通启动。

**处理**：保持服务停止写入，备份数据库，核对提示所指对象及 `__EFMigrationsHistory`。启动核验是只读事务，不执行 DDL、stamp 或 EF 迁移。只有五条历史的精确目标结构可直接启动。精确受支持的 1/2/3/4 历史库及已知三历史运行时结构按[部署说明](./Deployment.md)使用显式 `--upgrade-known-db`；未知、混合或旧 `public.mistake` 结构仍按 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 处理。不要删除旧表或手工补写历史。

### 5.4 空库需要显式升级

**症状**：启动提示 `Mistake database is empty; run --upgrade-empty-db`。

**处理**：确认连接的是预期的全新空库，停止应用写入并备份，再使用相同配置运行 `dotnet Ruoyu.Study.Mistake.Host.dll --upgrade-empty-db`。命令在锁和事务内创建目标结构，失败会回滚；成功后正常启动。它不处理旧库。普通启动账号只需读取 catalog、表和历史；显式升级账号另需 DDL 和写历史权限。

### 5.5 旧版本的列/历史冲突

旧启动路径可能添加了 `student_id`，但未记录 `20260612030015_AddStudentIdToProcessLog`；单有该列也不能证明 `image_paths` 已为 jsonb。新启动会拒绝此不一致结构，避免再次尝试 `ADD COLUMN`。保留备份，核对完整 schema 与历史，再按父项 IKJ0IK 处理，勿手工 stamp。

---

## 6. OSS 配置错误

### 6.1 S3OssService 初始化失败

**典型症状**：服务启动正常，但任何 OSS 操作（如 `CompleteUploadReview` 中的图片迁移）抛出异常。

**常见日志特征**：
- `Amazon.S3.AmazonS3Exception: The AWS Access Key Id you provided does not exist`
- `Amazon.S3.AmazonS3Exception: NotFound`
- 连接超时异常

**可能根因**：
- `Oss:InternalEndpoint` 配置错误（格式应为 `host:port`），或 `Oss:InternalSecure` 与服务协议不匹配
- `Oss:AccessKey` / `Oss:SecretKey` 无效
- `Oss:BucketName` 对应的 Bucket 不存在

**排查步骤**：
1. 检查 `appsettings.json` 中 `Oss` 配置节
2. 用 S3 客户端工具（如 `aws cli`、MinIO Console）验证连接
3. 确认 Bucket 存在：`aws s3 ls s3://ruoyu-study --endpoint-url http://localhost:8333`

**临时绕过**：设置环境变量 `USE_LOCAL_OSS=1` 切换到本地文件存储

**根治建议**：确保 S3 兼容服务正常运行，配置正确的凭证。

### 6.2 USE_LOCAL_OSS=1 但路径不可写

**典型症状**：设置 `USE_LOCAL_OSS=1` 后，OSS 操作报权限错误。

**常见日志特征**：
- `UnauthorizedAccessException: Access to the path '...' is denied`
- `DirectoryNotFoundException`

**可能根因**：
- `OSS_LOCAL_PATH` 环境变量指向的目录不存在或无写权限
- 默认路径 `data/oss` 不存在

**排查步骤**：
1. 检查 `OSS_LOCAL_PATH` 环境变量值
2. 确认目录存在且有写权限

**临时绕过**：手动创建目录并赋权

**根治建议**：在 `LocalFileOssService` 初始化时自动创建目录。

---

## 7. 配置缺失或端口错误

### 7.1 端口配置说明

**典型症状**：服务启动后无法从预期端口访问。

**常见日志特征**：无报错，但监听端口与预期不符

**可能根因**：
- `Mistake:Http:Port`（`Mistake__Http__Port`）覆盖了默认 5007，或显式值无效
- host 端口映射未配置（`start.sh` 的 `Port` 变量缺失或被占用）
- gRPC 端口 5006 已于 2026-07-12 下线，不再监听（见 [ADR-001](../overview/ADR-001-mistake-webapi-only.md)）

**排查步骤**：
1. 检查 `Mistake:Http:Port` 的最终值和启动日志中的 HTTP 端口；缺失时应为 5007，无效值会在数据库连接前报出键名
2. 确认容器发布端口映射到实际内部监听端口；默认 `start.sh` 为 `-p ${Port}:5007`
3. 确认调用方连接的端口与 mistake 实际监听端口一致

| 端口 | 协议 | 配置方式 |
|------|------|---------|
| 5007（容器内默认） | HTTP/1.1 | `Mistake:Http:Port`；环境键 `Mistake__Http__Port` |
| host 映射端口 | — | `start.sh` 的 `Port` 变量（`-p ${Port}:5007`） |

**回退**：移除端口覆盖，重启进程，并同步容器映射和调用方地址到 5007。

**根治建议**：仅用 `Mistake:Http:Port` 设置内部监听端口；`Endpoints:Http` 与 `ASPNETCORE_URLS` 不决定本服务端口。端口占用时排除占用或选择其他端口，并同步容器映射及 `MistakeService:Url`。

### 7.2 ConnectionStrings:Default 缺失

**典型症状**：服务启动失败，无法连接数据库。

**可能根因**：`ConnectionStrings:Default` 未配置且 Consul 未注入 `PostgreSql:*` 共享配置

**排查步骤**：
1. 检查 `appsettings.json` 中 `ConnectionStrings:Default` 是否存在且为 PostgreSQL 连接串
2. 确认 Consul 中 `PostgreSql:Host`/`Port`/`Username`/`Password` 是否已配置
3. 确认 `Database:Name` 配置正确

**临时绕过**：添加 PostgreSQL 连接字符串

**根治建议**：生产环境通过 Consul 配置 `PostgreSql:*`，本地开发在 `appsettings.json` 配置 PostgreSQL 连接串。

---

## 8. 历史脏数据/路径格式异常

### 8.1 source_regions 中路径不以 uploads/ 开头

**典型症状**：`CompleteUploadReview` 返回 `Success=true`，但部分图片的 `source_regions.SourceImagePath` 未被更新为 `mistakes/` 前缀。

**常见日志特征**：
- 无迁移日志（既无 `"Migrated image"` 也无 `"Error migrating image"`）
- 日志：`"Item {ItemId} is not Confirmed (status={Status}), will only remove from upload record, not migrate"`（`MistakeServiceImpl.cs` L296）

**可能根因**：
- 路径不以 `uploads/` 开头 → 代码 L290 跳过迁移（`if (!region.SourceImagePath.StartsWith("uploads/", ...)) continue;`）
- 错题不是 `Confirmed` 状态 → 仅从上传记录移除，不迁移（L293-299）
- 路径为空 → L282-283 跳过（`if (string.IsNullOrWhiteSpace(...)) continue;`）

**排查步骤**：
1. 查询数据库：`SELECT id, source_regions FROM mistake_item WHERE source_upload_id = '<id>'`
2. 检查 `source_regions` JSON 中 `sourceImagePath` 的值格式
3. 确认路径是否以 `uploads/` 开头（大小写不敏感）

**临时绕过**：手动更新数据库中的 `source_regions` 路径，使其以 `uploads/` 开头，然后重新执行 `CompleteUploadReview`

**根治建议**：在 `SubmitMistakeUpload` 和 `AddMistakeItem` 入口校验 `sourceImagePath` 格式，确保以 `uploads/` 开头。

### 8.2 旧表 `mistake` 数据待升级

当前启动初始化不会把 `mistake` 自动重命名为 `mistake_item`，也不会删除旧表、外键或旧列。若库中仍有旧表，先停止应用写入并备份，核对历史 schema、行和关联关系，再通过显式升级流程处理。旧表与新表同时存在也不得直接删除任一方。

---

## 9. 审核后上传记录状态未更新

### 9.1 审核确认后上传记录中仍保留已迁移图片

**典型症状**：ReviewMistakeItem 确认审核成功，但学生服务的上传记录中仍显示已迁移的图片。

**常见日志特征**：
- `"Error calling RemoveImagesFromRecord"`（MistakeServiceImpl.cs）
- 审核成功但无 RemoveImagesFromRecord 调用日志

**可能根因**：
- 学生服务 gRPC 不可用，RemoveImagesFromRecord 调用失败
- source_upload_id 为空字符串（旧数据），导致调用参数无效
- 学生服务内部错误

**排查步骤**：
1. 查看 mistake 服务日志中是否有 RemoveImagesFromRecord 调用失败的 error 日志
2. 确认学生服务是否正常运行
3. 检查 source_upload_id 值是否有效（非空、格式正确）
4. 确认学生服务侧 RemoveImagesFromRecord 的处理逻辑

**临时绕过**：手动调用学生服务 API 移除图片引用

**根治建议**：增加补偿任务，定期扫描已确认但上传记录未清理的图片引用并重试。

### 9.2 全部审核完成后上传记录未标记为已退回

**典型症状**：某上传记录下所有错题均已审核（无 PendingReview 条目），但上传记录状态仍为 UnderReview（原 Completed），未变为 Returned。

**常见日志特征**：
- `"Error calling MarkUploadRecordAsReturned"`（MistakeServiceImpl.cs）
- 审核成功但无 MarkUploadRecordAsReturned 调用日志

**可能根因**：
- 学生服务 gRPC 不可用，MarkUploadRecordAsReturned 调用失败
- source_upload_id 为空字符串（旧数据），导致调用参数无效
- 仍有 PendingReview 状态的条目（其他错题尚未审核），不满足调用条件

**排查步骤**：
1. 调用 `GetMistakeItemsByUpload(sourceUploadId)` 确认该上传下是否仍有 PendingReview 条目
2. 若无 PendingReview 条目但未标记，查看 mistake 服务日志中是否有 MarkUploadRecordAsReturned 调用失败的 error 日志
3. 确认学生服务是否正常运行
4. 检查 source_upload_id 值是否有效

**临时绕过**：手动调用学生服务 API 标记上传记录为已退回

**根治建议**：增加补偿任务，定期扫描无待审核条目但未标记为已退回的上传记录并重试。

> **设计说明**: 审核后编排（RemoveImagesFromRecord、MarkUploadRecordAsReturned）由 mistake 服务的 gRPC 层直接调用，不再由 teacher_portal Controller 协调。这些调用失败不影响审核状态的持久化，但需通过补偿机制保证最终一致性。

---

## 快速参考：关键配置项

| 配置项 | 位置 | 默认值 | 说明 |
|--------|------|--------|------|
| HTTP 监听端口 | `Mistake:Http:Port` / `Mistake__Http__Port` | 5007 | HTTP/1.1 端口（容器内默认） |
| `Port` | `start.sh` 变量 | 5007 | host 端口映射（`-p ${Port}:5007`） |
| `ConnectionStrings:Default` | appsettings.json | （无） | PostgreSQL 连接字符串（本地开发兜底） |
| `Oss:InternalEndpoint` | appsettings.json / Consul | localhost:8333 | 后端实际连接的 S3 地址 |
| `Oss:InternalSecure` | appsettings.json / Consul | false | 内部 S3 连接是否使用 HTTPS |
| `Oss:PublicBaseUrl` | appsettings.json / Consul | https://ry.zhoufan.asia/oss | 浏览器使用的预签名公共基础 URL |
| `Oss:AccessKey` | appsettings.json | mock_access_key | OSS AccessKey |
| `Oss:SecretKey` | appsettings.json | mock_secret_key | OSS SecretKey |
| `Oss:BucketName` | appsettings.json | ruoyu-study | OSS Bucket 名称 |
| `USE_LOCAL_OSS` | 环境变量 | 未设置 | 设为 `1` 使用本地文件存储 |
| `OSS_LOCAL_PATH` | 环境变量 | data/oss | 本地 OSS 存储路径 |

## 快速参考：关键日志消息

| 日志消息 | 位置 | 含义 |
|----------|------|------|
| `"Mistake gRPC host is running."` | Program.cs L86 | 服务正常启动 |
| `"Migrated image from {OldPath} to {NewPath}"` | MistakeServiceImpl.cs L322 | OSS 图片迁移成功 |
| `"Error migrating image {Path}"` | MistakeServiceImpl.cs L328 | OSS 图片迁移失败 |
| `"Mistake database schema verified for read-only startup"` | DatabaseInitializer.cs | 启动已完成只读结构核验 |

### 重分析任务停滞或迁移结果不明

先检查全部 Mistake 实例的重分析 worker 开关。有效 Running 由 `lease_until > clock_timestamp()` 判定；健康 worker 每 20 秒续租，租约 90 秒。单个失联 Job 到期可由其他实例重领，最多三次；旧 owner 的迟到结果会被栅栏拒绝。若数据库不可用或重领持续失败，关闭所有重分析 worker，保留 Job 行及日志，核查数据库连接和权限后再启用。不要手工把 Running 改 Pending、删 Job 或改 `owner_id`/`fencing_version`。

升级发生连接断开时保持停写，以只读连接核验 `__EFMigrationsHistory` 是否已有第五条 `20260930000001_LeaseReanalyzeJobs`、六个租约/执行字段、旧 Running 的 `upgrade_interrupted` 状态和关联日志；五历史精确目标可重复执行显式升级。结构不匹配时保留备份并停止切换，按[部署流程](./Deployment.md)处理。旧二进制不能直接对第五历史库回退运行。
