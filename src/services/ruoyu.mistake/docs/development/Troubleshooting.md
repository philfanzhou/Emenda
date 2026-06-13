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
- `"审核结果只能是确认或驳回"` — `review_status` 不是 `Confirmed` 或 `Rejected`（`MistakeServiceImpl.cs` L146）
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
4. 检查 `Oss` 配置节（`Endpoint`/`AccessKey`/`SecretKey`/`BucketName`）是否正确

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

**临时绕过**：删除 `ConnectionStrings:Default` 配置，服务会回退到 SQLite 模式

**根治建议**：确保 PostgreSQL 服务稳定运行，配置正确的连接字符串。

### 5.2 SQLite 目录不存在

**典型症状**：未配置 PostgreSQL 连接字符串时，服务启动报 SQLite 文件路径错误。

**常见日志特征**：
- `Microsoft.Data.Sqlite.SqliteException: SQLite Error 14: 'unable to open database file'`

**可能根因**：SQLite 默认路径 `data/sqlite/ruoyu_study_mistake.db` 的父目录 `data/sqlite/` 不存在

**排查步骤**：
1. 检查工作目录下是否存在 `data/sqlite/` 目录
2. 手动创建：`mkdir -p data/sqlite`

**临时绕过**：手动创建 `data/sqlite/` 目录

**根治建议**：在 `Program.cs` 中启动前自动创建 SQLite 目录。

### 5.3 旧表迁移失败

**典型症状**：服务启动时 `DatabaseInitializer.InitializeAsync` 抛出 SQL 执行异常。

**常见日志特征**：
- `MigrateOldSchemaAsync` 中的 SQL 执行异常
- 正常日志：`"检测到旧表 mistake，正在迁移到新表结构..."`（`DatabaseInitializer.cs` L98）
- 正常日志：`"旧表迁移完成"`（L184）

**可能根因**：
- 旧表 `mistake` 中存在与新表结构冲突的约束
- `ALTER TABLE` 操作被其他连接锁定
- `__EFMigrationsHistory` 操作异常

**排查步骤**：
1. 查看启动日志中是否有 `"检测到旧表 mistake"` 的日志
2. 检查 PostgreSQL 中是否存在 `mistake` 旧表：`SELECT * FROM information_schema.tables WHERE table_name = 'mistake'`
3. 检查 `__EFMigrationsHistory` 表状态

**临时绕过**：手动执行迁移 SQL（参考 `DatabaseInitializer.cs` 中的语句），然后插入迁移 stamp

**根治建议**：确保启动时无其他连接占用旧表；生产环境在部署前备份数据库。

### 5.4 迁移 stamp 失败

**典型症状**：服务每次启动都尝试执行 EF Core 迁移，或报 `__EFMigrationsHistory` 操作异常。

**常见日志特征**：
- `"已将 InitialCreate 迁移标记为已应用"`（`DatabaseInitializer.cs` L70）— 正常
- 若无此日志但表已存在，说明 stamp 未写入

**可能根因**：
- `__EFMigrationsHistory` 表创建失败（权限不足）
- stamp 插入被回滚（事务冲突）

**排查步骤**：
1. 查询 `SELECT * FROM "__EFMigrationsHistory"` 确认是否有 `20260514051544_InitialCreate` 记录
2. 若无，手动插入：`INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20260514051544_InitialCreate', '8.0.0')`

**临时绕过**：手动插入迁移 stamp

**根治建议**：确保数据库用户有创建表和插入数据的权限。

### 5.5 迁移列已存在导致启动失败

**典型症状**：服务启动时报 `42701: column "student_id" of relation "process_log" already exists`，服务崩溃。

**常见日志特征**：
- `Failed executing DbCommand ... ALTER TABLE process_log ADD student_id character varying(36)`
- `Npgsql.PostgresException: 42701: column "student_id" of relation "process_log" already exists`

**可能根因**：
- `EnsureColumnRenamesAsync` 在启动时已通过 `IF NOT EXISTS` 安全地添加了 `student_id` 列
- `StampMigrationsIfTablesExistAsync` 只标记了 `InitialCreate`，未标记 `AddStudentIdToProcessLog` 迁移
- `MigrateAsync` 发现该迁移为 pending，尝试执行 `ALTER TABLE ADD COLUMN`，但列已存在

**排查步骤**：
1. 查询 `SELECT * FROM "__EFMigrationsHistory"` 确认是否缺少 `20260612030015_AddStudentIdToProcessLog` 记录
2. 查询 `SELECT column_name FROM information_schema.columns WHERE table_name = 'process_log' AND column_name = 'student_id'` 确认列是否已存在
3. 如果列已存在但迁移未标记，说明 stamp 逻辑未覆盖此迁移

**临时绕过**：手动插入迁移 stamp：
```sql
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260612030015_AddStudentIdToProcessLog', '8.0.0');
```

**根治建议**：确保 `StampMigrationsIfTablesExistAsync` 中包含对 `AddStudentIdToProcessLog` 迁移的检测逻辑，当 `student_id` 列已存在时自动标记该迁移为已应用。

---

## 6. OSS 配置错误

### 6.1 S3OssService 初始化失败

**典型症状**：服务启动正常，但任何 OSS 操作（如 `CompleteUploadReview` 中的图片迁移）抛出异常。

**常见日志特征**：
- `Amazon.S3.AmazonS3Exception: The AWS Access Key Id you provided does not exist`
- `Amazon.S3.AmazonS3Exception: NotFound`
- 连接超时异常

**可能根因**：
- `Oss:Endpoint` 配置错误（如缺少协议前缀、端口错误）
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

### 7.1 Endpoints 配置缺失

**典型症状**：服务启动后无法从预期端口访问。

**常见日志特征**：无报错，但监听端口与预期不符

**可能根因**：
- `Endpoints:Grpc` 缺失 → 默认 5006
- `Endpoints:Http` 缺失 → 默认 5007
- 配置键名拼写错误

**排查步骤**：
1. 检查 `appsettings.json` 中是否有 `Endpoints` 配置节
2. 启动日志中查看 Kestrel 实际监听端口
3. 确认调用方连接的端口与 mistake 实际监听端口一致

| 配置键 | 默认值 | 协议 |
|--------|--------|------|
| `Endpoints:Grpc` | 5006 | HTTP/2 |
| `Endpoints:Http` | 5007 | HTTP/1.1 |

**临时绕过**：使用默认端口

**根治建议**：在 `appsettings.json` 中显式配置端口，避免依赖默认值。

### 7.2 ConnectionStrings:Default 缺失

**典型症状**：服务使用 SQLite 而非 PostgreSQL，数据未持久化或与预期不一致。

**常见日志特征**：
- 无 PostgreSQL 相关日志
- SQLite 文件出现在工作目录下

**可能根因**：`ConnectionStrings:Default` 未配置，回退到 SQLite 默认路径 `Data Source=data/sqlite/ruoyu_study_mistake.db`

**排查步骤**：
1. 检查 `appsettings.json` 中 `ConnectionStrings:Default` 是否存在
2. 确认 `Program.cs` L43-51 的判断逻辑：连接字符串包含 `Host=` 或 `Server=` 才使用 PostgreSQL

**临时绕过**：添加 PostgreSQL 连接字符串

**根治建议**：生产环境必须配置 PostgreSQL 连接字符串，SQLite 仅用于开发/测试。

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

### 8.2 旧表 mistake 的数据迁移

**典型症状**：从旧表 `mistake` 迁移后，部分字段值不符合预期。

**常见日志特征**：
- `"检测到旧表 mistake，正在迁移到新表结构..."`（`DatabaseInitializer.cs` L98）
- `"mistake_item 已存在，删除旧表 mistake"`（L131）
- `"旧表迁移完成"`（L184）

**可能根因**：
- 旧表 `mistake.status` → 新表 `mistake_item.review_status`：列名重命名（L152-162）
- 旧表 `process_log.mistake_id` → `process_log.mistake_item_id`：列名重命名（L214-225）
- 旧表 `mistake_practice.mistake_id` → `mistake_practice.mistake_item_id`：列名重命名（L244-255）
- 新增列的默认值：`source_upload_id = ''`、`source_regions = '[]'`、`type = 0`

**排查步骤**：
1. 检查迁移后的表结构：`\d mistake_item`、`\d process_log`、`\d mistake_practice`
2. 确认列名是否正确：`review_status`（非 `status`）、`mistake_item_id`（非 `mistake_id`）
3. 检查 `source_upload_id` 是否为空字符串（旧数据无此字段，默认为 `''`）

**临时绕过**：手动执行 SQL 修正列名或补充默认值

**根治建议**：迁移完成后验证数据完整性，特别是 `source_upload_id` 为空的记录需手动补充。

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
| `Endpoints:Grpc` | appsettings.json | 5006 | gRPC (HTTP/2) 端口 |
| `Endpoints:Http` | appsettings.json | 5007 | HTTP/1.1 端口 |
| `ConnectionStrings:Default` | appsettings.json | SQLite | PostgreSQL 连接字符串 |
| `Oss:Endpoint` | appsettings.json | localhost:8333 | S3 兼容 OSS 端点 |
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
| `"检测到旧表 mistake，正在迁移到新表结构..."` | DatabaseInitializer.cs L98 | 旧表迁移开始 |
| `"旧表迁移完成"` | DatabaseInitializer.cs L184 | 旧表迁移完成 |
| `"已将 InitialCreate 迁移标记为已应用"` | DatabaseInitializer.cs L70 | 迁移 stamp 写入成功 |
