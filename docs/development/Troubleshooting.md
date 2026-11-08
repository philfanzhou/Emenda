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

**排查步骤**：
1. 确认 OSS 服务可用：检查 S3 endpoint 可达性与凭据
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

**症状**：启动失败，错误以 `Mistake database incompatible:` 开头并指出缺少或多出的关系、列、默认值、索引、约束或迁移历史，结尾为 `Automatic upgrade does not support this structure; database unchanged.`。不符合已知形态的 `public.mistake`、只有部分表、额外对象以及列类型或历史不一致都会这样拒绝。

**处理**：数据库未被修改。核对提示所指对象及 `__EFMigrationsHistory`。空库、已知 1–10 条 EF 历史和[旧版自动维护留下的已知结构](../database/migrations.md#旧版自动维护结构的收敛)会在启动时自动升级，不会出现此错误。消息末尾带 `Legacy convergence was rolled back.` 时，表示识别到旧代码标志并尝试收敛，但收敛后仍有对象对不上已知形态（例如陌生表、列或索引），收敛已整体回滚。旧表或旧列上附加了陌生索引、约束、触发器时，提示为 `Mistake legacy convergence found unexpected object <名称> ...`。04-19 至 05-13 的精确旧表自动转换；陌生列、索引或触发器会在任何写入前按 `legacy relation public.mistake exists` 拒绝，见[旧表转换](../database/migrations.md#旧表-mistake-的转换)。不要删除旧表、手工改列或补写历史。

### 5.4 自动升级失败

**症状**：启动提示 `Mistake database upgrade from version N failed before commit (...)` 或 `Mistake legacy schema convergence failed before commit (...)`，括号内为本服务自身的核验结论或 `SQLSTATE xxxxx`。

**处理**：升级事务已整体回滚，数据库保持升级前状态。按 SQLSTATE 排查：`42501` 为权限不足，升级需要 DDL 与写迁移历史的权限；`25006` 为只读连接或只读事务；`57014` 为宿主停止或取消；其他值按 PostgreSQL 错误码核对。升级不受连接串 `Command Timeout` 限制，大库耗时长时应放宽启动等待，不要反复杀进程。练习路径不是 JSON 字符串数组的行不会再使升级失败：原文写入 `legacy_archive`，该行改为 `[]`，日志 `Mistake legacy schema converged: ...` 给出条数。修正后再次启动即重新分类并重试。COMMIT 确认丢失时同样直接重启：已提交则按目标启动，未提交则重新升级。

### 5.5 旧版本的列/历史冲突

旧启动路径可能添加了 `student_id`，但未记录 `20260612030015_AddStudentIdToProcessLog`；单有该列也不能证明 `image_paths` 已为 jsonb。已知旧形态（含加外键失败、历史只剩 0501/0503 的库）在启动时自动收敛；不属于已知形态的此类结构仍拒绝，避免再次尝试 `ADD COLUMN`。核对完整 schema 与历史，勿手工 stamp。被收敛移除或改写的旧数据可在 `legacy_archive` 按 `source_table`、`source_key` 查到，见[归档规则](../database/migrations.md#归档规则)。

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

**临时绕过**：无本地文件回退。Mistake Host 无条件使用 `S3OssService`；`USE_LOCAL_OSS` / `OSS_LOCAL_PATH` 是 Student Host 的开发用途开关，对 Mistake 无效。

**根治建议**：确保 S3 兼容服务正常运行，配置正确的凭证。

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

受支持的 04-19 至 05-13 旧表在启动事务内转换为当前条目，保留 ID、关系和图片，原行归档；两表并存时现条目 ID 优先。转换失败整体回滚，可修正权限或故障后重启；陌生结构保持原状拒绝。不要手工删除任一表，完整边界见[旧表转换](../database/migrations.md#旧表-mistake-的转换)。

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

## 快速参考：关键日志消息

| 日志消息 | 位置 | 含义 |
|----------|------|------|
| `"Mistake gRPC host is running."` | Program.cs L86 | 服务正常启动 |
| `"Migrated image from {OldPath} to {NewPath}"` | MistakeServiceImpl.cs L322 | OSS 图片迁移成功 |
| `"Error migrating image {Path}"` | MistakeServiceImpl.cs L328 | OSS 图片迁移失败 |
| `"Mistake database schema verified at target version {TargetVersion}; no upgrade needed"` | DatabaseInitializer.cs | 已是目标结构，启动未写入 |
| `"Mistake database schema verified at target version {TargetVersion} after automatic upgrade from version {FromVersion}; steps: ..."` | DatabaseInitializer.cs | 启动时已自动升级并核验 |
| `"Mistake legacy schema converged: {ArchivedRows} removed or rewritten rows/values archived in legacy_archive; {ResetImagePaths} invalid mistake_practice.image_paths values reset to []"` | DatabaseInitializer.cs | 收敛了旧结构或重置了非法练习路径（Warning） |

### 重分析任务停滞或迁移结果不明

先检查全部 Mistake 实例的重分析 worker 开关。有效 Running 由 `lease_until > clock_timestamp()` 判定；健康 worker 每 20 秒续租，租约 90 秒。单个失联 Job 到期可由其他实例重领，最多三次；旧 owner 的迟到结果会被栅栏拒绝。若数据库不可用或重领持续失败，关闭所有重分析 worker，保留 Job 行及日志，核查数据库连接和权限后再启用。不要手工把 Running 改 Pending、删 Job 或改 `owner_id`/`fencing_version`。

升级发生连接断开时直接重新启动：启动会重新分类，已提交则按目标启动，未提交则重新升级。如需人工确认，可核验 `__EFMigrationsHistory`、租约/执行字段、旧 Running 的 `upgrade_interrupted` 状态和关联日志。结构不匹配时停止切换，按[部署流程](./Deployment.md)处理。旧版本不能直接对已升级的库回退运行。
