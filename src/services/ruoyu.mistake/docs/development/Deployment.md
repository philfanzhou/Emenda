# ruoyu.mistake 部署集成说明

本文档描述 `ruoyu.mistake` 服务的完整部署流程，包括配置准备、数据库初始化、OSS 准备、依赖服务协调、启动验证等环节。

---

## Consul 接入约束

`ruoyu.mistake` 现在固定接入 Consul，通过 `ruoyu.common` 下的共享 Consul 代码读取共享配置并注册服务。

共享 Consul 配置承载：

- `config/ruoyu/shared.json`
  - `PostgreSql:Host`
  - `PostgreSql:Port`
  - `PostgreSql:Username`
  - `PostgreSql:Password`
  - `Oss:*`
- `config/ruoyu/service-endpoints.json`
  - `StudentService:GrpcUrl`

服务私有配置仍保留在 Mistake 自身：

- `Database:Name=ruoyu_study_mistake`
- `Endpoints:Grpc=5006`
- `Endpoints:Http=5007`
- `VlAnalyzer:*`

启动脚本 `start.sh` 只保留：

- `CONSUL_HTTP_ADDR`
- `CONSUL_TOKEN`
- `Endpoints__Grpc`
- `Endpoints__Http`
- `Database__Name`
- `VlAnalyzer__ApiKey`
- `VlAnalyzer__BaseUrl`
- `VlAnalyzer__Model`

并补充：

```bash
--add-host=host.docker.internal:host-gateway
```

确保 Linux Docker 容器能访问宿主机上的 Consul。

---

## 端口与协议说明

| 端口 | 协议 | 用途 | 配置键 | 默认值 |
|------|------|------|--------|--------|
| gRPC 端口 | HTTP/2 | 接收所有 gRPC 调用（student、admin_portal、teacher_portal、user_portal） | `Endpoints:Grpc` | 5006 |
| HTTP 端口 | HTTP/1 | 健康检查（`GET /`） | `Endpoints:Http` | 5007 |

**gRPC 端口**承载全部业务接口，包括 `SubmitMistakeUpload`、`GetMistakeItem`、`ReviewMistakeItem`、`CompleteUploadReview` 等 15 个 RPC 方法。

**HTTP 端口**仅用于健康检查，`GET /` 返回 `"Mistake gRPC host is running."`。student 服务的 `GrpcUploadDispatcher` 通过此端点判断 mistake 服务是否可用。

---

## 流程一：配置准备

| 项目 | 内容 |
|------|------|
| **节点名称** | 配置准备 |
| **前置条件** | 已获取部署环境信息（Consul 地址、ACL token、VL 分析私有配置） |
| **输入条件** | appsettings.json 模板、环境变量清单 |
| **操作步骤** | 1. 配置 `CONSUL_HTTP_ADDR` 和 `CONSUL_TOKEN`<br>2. 确认 Consul 中已存在 `PostgreSql:*`、`Oss:*`、`StudentService:GrpcUrl`<br>3. 在 Mistake 自身配置中保留 `VlAnalyzer:*`、`Database:Name`、`Endpoints:*`<br>4. 如需自定义端口，设置 `Endpoints:Grpc` 和 `Endpoints:Http` |
| **成功判定** | 所有必填配置项均有有效值；端口不与已有服务冲突 |
| **失败后排查方向** | 1. 配置键拼写错误 → 对照 appsettings.json 结构检查<br>2. 端口冲突 → `netstat` 检查端口占用<br>3. 环境变量未生效 → 确认运行环境是否正确注入 |

### 配置项速查

```json
{
  "Consul": {
    "Host": "host.docker.internal",
    "Port": 8500,
    "KvPrefix": "config/ruoyu"
  },
  "Endpoints": {
    "Grpc": 5006,
    "Http": 5007
  },
  "Database": {
    "Name": "ruoyu_study_mistake"
  },
  "VlAnalyzer": {
    "BaseUrl": "https://api.siliconflow.cn/v1",
    "Model": "Qwen/Qwen3-VL-30B-A3B-Instruct"
  }
}
```

### 数据库类型判定逻辑

连接字符串包含 `Host=` 或 `Server=`（不区分大小写）→ 使用 PostgreSQL（Npgsql），否则使用 SQLite。

SQLite 未配置连接字符串时，默认路径为 `data/sqlite/ruoyu_study_mistake.db`。

容器部署下默认通过 Consul 中的 `PostgreSql:*` 与本地 `Database:Name` 拼出 PostgreSQL 连接串。

### OSS 模式判定逻辑

| 环境变量 | OSS 实现 | 存储路径 |
|----------|---------|---------|
| `USE_LOCAL_OSS=1` | `LocalFileOssService` | `OSS_LOCAL_PATH` 环境变量指定，默认 `data/oss` |
| 未设置或非 `1` | `S3OssService` | 由 `Oss:Endpoint` + `Oss:BucketName` 决定 |

---

## 流程二：数据库准备

| 项目 | 内容 |
|------|------|
| **节点名称** | 数据库准备 |
| **前置条件** | 配置准备完成；数据库服务器已运行（PostgreSQL 模式）或文件系统可写（SQLite 模式） |
| **输入条件** | 有效的 `ConnectionStrings:Default` |
| **操作步骤** | 1. PostgreSQL 模式：创建数据库 `ruoyu_study_mistake`（或连接串中指定的数据库名）<br>2. SQLite 模式：确保 `data/sqlite/` 目录存在且可写<br>3. 启动服务，`DatabaseInitializer` 将自动完成建表和迁移 |
| **成功判定** | 服务启动日志中出现初始化完成信息，无异常；数据库中存在 `mistake_item`、`mistake_practice`、`process_log` 三张表 |
| **失败后排查方向** | 1. 连接失败 → 检查连接串、数据库是否已创建、网络是否可达<br>2. 权限不足 → 检查数据库用户是否有 CREATE TABLE 权限<br>3. 迁移失败 → 查看启动日志中的异常堆栈，见下方"数据库迁移失败判断" |

### PostgreSQL 自动迁移逻辑

服务启动时 `DatabaseInitializer.InitializeAsync` 按以下顺序执行：

1. **旧表迁移**：检测 `mistake` 表是否存在
   - 存在 → 重命名为 `mistake_item`，添加缺失列（`source_upload_id`、`source_regions`、`reviewer_id`、`reviewed_at`、`root_cause`、`type`、`question_id`），将 `status` 列重命名为 `review_status`，删除旧列（`date`、`image_paths`、`fingerprint`），创建索引
   - 不存在 → 跳过
2. **列名修正**：检测 `process_log` 和 `mistake_practice` 表中是否存在 `mistake_id` 列，存在则重命名为 `mistake_item_id`
3. **外键补全**：为 `process_log` 和 `mistake_practice` 添加指向 `mistake_item` 的外键约束（如不存在）
4. **迁移标记**：如果 `mistake_item` 表已存在但 `__EFMigrationsHistory` 中没有 `20260514051544_InitialCreate` 记录，自动插入标记行
5. **建表保障**：通过 `Common.Database.DatabaseInitializer.InitializeAsync` 确保三张表存在

### SQLite 自动初始化逻辑

SQLite 模式跳过上述全部迁移逻辑，直接调用 `EnsureCreated` 创建表结构。

### SQLite 兼容性已知限制

> **设计决策**：以下 workaround 是已知且有意为之的，生产环境使用 PostgreSQL 不受影响。

| 位置 | 限制 | Workaround | 影响 |
|------|------|-----------|------|
| `MistakeItemRepository.GetPendingReviewUploadsAsync` | SQLite 不支持 `DateTimeOffset` 的 `Min` 聚合 | 在客户端执行分组和聚合 | 开发环境下数据量大时性能较低，生产 PostgreSQL 不受影响 |
| `MistakeItemRepository.GetListAsync` | SQLite 不支持 `DateTimeOffset` 的 `ORDER BY` | 在客户端排序 | 开发环境下分页排序在客户端完成，生产 PostgreSQL 在数据库端排序 |

### 数据库不存在或迁移失败时怎么判断

| 现象 | 原因 | 判断方法 |
|------|------|---------|
| 服务启动抛出 `NpgsqlException` | PostgreSQL 未运行或连接串错误 | 查看启动日志中的异常信息，确认 Host/Port/Username/Database |
| 服务启动抛出 `SqliteException` | SQLite 文件路径不可写 | 检查 `data/sqlite/` 目录权限 |
| 日志出现"检测到旧表 mistake，正在迁移到新表结构..."后异常 | 旧表结构与迁移脚本不兼容 | 检查旧表列定义，确认迁移 SQL 中的 `IF EXISTS` / `IF NOT EXISTS` 逻辑是否覆盖 |
| 日志出现"已将 InitialCreate 迁移标记为已应用" | 正常，表示已有表被标记 | 无需处理 |
| 表存在但 `__EFMigrationsHistory` 为空且未 stamp | stamp 逻辑未执行 | 手动检查 `mistake_item` 表是否存在，确认 stamp SQL 是否被跳过 |
| EF Core 后续 `Migrate()` 报错"已有列" | stamp 未正确标记，EF 尝试重复迁移 | 手动在 `__EFMigrationsHistory` 中插入 `20260514051544_InitialCreate` 记录 |

---

## 流程三：OSS 准备

| 项目 | 内容 |
|------|------|
| **节点名称** | OSS 准备 |
| **前置条件** | 配置准备完成 |
| **输入条件** | S3 兼容存储服务已部署（生产）或本地文件系统可写（开发） |
| **操作步骤** | 1. 生产模式：确认 MinIO/SeaweedFS 已运行，`Oss:BucketName` 指定的桶已创建<br>2. 开发模式：设置 `USE_LOCAL_OSS=1`，确认 `data/oss` 目录可写<br>3. 验证凭证有效性（生产模式） |
| **成功判定** | 服务启动后，涉及 OSS 的操作（如 `CompleteUploadReview`）可正常执行 |
| **失败后排查方向** | 1. S3 连接失败 → 检查 `Oss:Endpoint` 是否可达、AccessKey/SecretKey 是否正确<br>2. Bucket 不存在 → 手动创建或确认自动创建策略<br>3. 本地 OSS 路径不可写 → 检查目录权限 |

### OSS 不可用时哪些功能受影响

| 功能 | 是否受影响 | 说明 |
|------|-----------|------|
| `SubmitMistakeUpload` | 不受影响 | 仅创建数据库记录，不操作 OSS |
| `GetMistakeItem` / `GetMistakeItemList` | 不受影响 | 仅读取数据库 |
| `ReviewMistakeItem` | 不受影响 | 仅更新数据库审核状态；审核确认后的图片迁移失败不阻塞审核状态更新，路径保留 uploads/ 前缀 |
| `CompleteUploadReview` | **部分受影响** | 需要将图片从 `uploads/` 复制到 `mistakes/`，然后删除 `uploads/` 原图。OSS 不可用时 Copy 失败，图片路径不更新；Copy 成功但 Delete 失败时路径仍会更新为 mistakes/ 前缀 |
| `GetAllReferencedImagePaths` | 不受影响 | 仅读取数据库中的路径字符串 |
| `AddMistakeItem` / `UpdateMistakeItem` / `DeleteMistakeItem` | 不受影响 | 仅操作数据库 |
| `AddMistakePractice` / `GetMistakePractices` | 不受影响 | 仅操作数据库 |
| `LogMistakeProcess` / `GetMistakeProcessHistory` | 不受影响 | 仅操作数据库 |

**结论**：OSS 不可用时，错题的创建、查询、审核状态变更等核心功能正常。图片迁移（Copy）失败时不阻塞审核流程，路径保留 `uploads/` 前缀待后续重试；Copy 成功但 Delete 失败时路径更新为 `mistakes/` 前缀，残留源文件可由 OssAuditWorker 清理。

---

## 流程四：依赖服务准备

| 项目 | 内容 |
|------|------|
| **节点名称** | 依赖服务准备 |
| **前置条件** | 各 portal 服务已部署 |
| **输入条件** | mistake 服务的 gRPC 地址 |
| **操作步骤** | 1. 确认 mistake 服务已启动并在 gRPC 端口（默认 5006）监听<br>2. 确认各 portal 的 mistake 地址配置正确 |
| **成功判定** | 各 portal 可正常调用 mistake 的 gRPC 接口 |
| **失败后排查方向** | 1. portal 地址不可达 → 检查地址配置、网络连通性<br>2. gRPC 调用超时 → 检查 mistake 是否在 HTTP/2 模式监听<br>3. 调用返回错误 → 查看 portal 日志中的 gRPC 错误详情 |

### 各 portal 的 mistake 地址配置

| Portal | 配置键 | 默认值 |
|--------|--------|--------|
| admin_portal | `MistakeGrpcService:Address` | `http://localhost:5006` |
| teacher_portal | `MistakeService:GrpcUrl` | `http://localhost:5006` |
| user_portal | `MistakeService:GrpcUrl` | `http://localhost:5006` |

如果 mistake 的 gRPC 端口不是默认的 5006，需要同步修改所有 portal 的配置。

---

## 流程五：服务启动

| 项目 | 内容 |
|------|------|
| **节点名称** | 服务启动 |
| **前置条件** | 配置准备、数据库准备、OSS 准备、依赖服务准备均已完成 |
| **输入条件** | 编译产物、appsettings.json、环境变量 |
| **操作步骤** | 1. 确认编译产物完整<br>2. 确认 appsettings.json 已正确配置<br>3. 如需本地 OSS，设置环境变量 `USE_LOCAL_OSS=1`<br>4. 执行 `dotnet Ruoyu.Study.Mistake.Host.dll`（或对应可执行文件）<br>5. 观察启动日志 |
| **成功判定** | 日志中出现监听端口信息，无异常；数据库初始化日志正常 |
| **失败后排查方向** | 1. 端口绑定失败 → 检查端口占用<br>2. 数据库连接失败 → 检查连接串和数据库状态<br>3. 依赖注入失败 → 检查配置项完整性 |

### 启动顺序

```
1. PostgreSQL / 确保文件系统可写（SQLite）
2. OSS 服务（MinIO/SeaweedFS）或确认本地目录可写
3. student 服务（不强制先于 mistake，但建议先启动）
4. mistake 服务
5. 各 portal 服务
```

### gRPC 消息大小

服务配置 gRPC 最大收发消息大小为 **100MB**（`MaxReceiveMessageSize` 和 `MaxSendMessageSize` 均为 100 * 1024 * 1024）。如果上传的图片数据超过此限制，gRPC 调用会失败。

---

## 流程六：健康检查

| 项目 | 内容 |
|------|------|
| **节点名称** | 健康检查 |
| **前置条件** | 服务已启动 |
| **输入条件** | HTTP 端口可访问 |
| **操作步骤** | 1. 向 HTTP 端口发送 `GET /` 请求<br>2. 检查响应状态码和内容 |
| **成功判定** | HTTP 200，响应体为 `"Mistake gRPC host is running."` |
| **失败后排查方向** | 1. 无响应 → 服务未启动或 HTTP 端口配置错误<br>2. 连接拒绝 → 端口未监听，检查 Kestrel 配置<br>3. 返回非预期内容 → 确认请求的是 HTTP 端口而非 gRPC 端口 |

### 健康检查方式

```bash
curl http://<host>:5007/
```

预期返回：`Mistake gRPC host is running.`

> **注意**：健康检查走 HTTP 端口（默认 5007），不是 gRPC 端口（默认 5006）。student 服务的 `GrpcUploadDispatcher` 通过此 HTTP 健康检查判断 mistake 是否可用。

---

## 流程七：联调顺序

| 项目 | 内容 |
|------|------|
| **节点名称** | 联调顺序 |
| **前置条件** | 所有服务已单独启动并通过健康检查 |
| **输入条件** | 各服务地址配置已互相指向 |
| **操作步骤** | 按以下顺序逐步验证联调 |
| **成功判定** | 每步验证均通过 |
| **失败后排查方向** | 见各步骤说明 |

### 联调验证步骤

| 步骤 | 验证内容 | 方法 | 失败排查 |
|------|---------|------|---------|
| 1 | student → mistake 连通 | student 端触发上传，观察 mistake 日志是否收到 `SubmitMistakeUpload` | 检查 student 的 mistake 地址配置、gRPC 端口是否正确 |
| 2 | portal → mistake 连通 | 通过 portal 页面查询错题列表，观察是否返回数据 | 检查 portal 的 mistake 地址配置 |
| 3 | mistake → OSS 操作 | 执行 `CompleteUploadReview`，确认图片从 `uploads/` 迁移到 `mistakes/` | 检查 OSS 配置和桶权限 |
| 4 | admin_portal OSS 审计 | 触发 `GetAllReferencedImagePaths`，确认返回图片路径列表 | 检查 admin_portal 的 mistake 地址配置 |

### 推荐联调顺序

```
student → mistake（SubmitMistakeUpload）
    ↓
teacher_portal → mistake（GetPendingReviewUploads → ReviewMistakeItem）
    ↓
teacher_portal → mistake → OSS（CompleteUploadReview）
    ↓
admin_portal → mistake（GetMistakeItemList、GetAllReferencedImagePaths）
    ↓
user_portal → mistake（GetMistakeItemList、DeleteMistakeItem）
```

---

## 流程八：上线后核验点

| 项目 | 内容 |
|------|------|
| **节点名称** | 上线后核验点 |
| **前置条件** | 服务已上线，有真实流量 |
| **输入条件** | 监控数据、日志、业务反馈 |
| **操作步骤** | 逐项检查以下核验点 |
| **成功判定** | 所有核验点通过 |
| **失败后排查方向** | 见各核验点说明 |

### 核验清单

| # | 核验项 | 检查方法 | 预期结果 | 异常排查 |
|---|--------|---------|---------|---------|
| 1 | 服务存活 | `GET /` 健康检查 | 返回 200 | 检查进程状态、端口监听 |
| 2 | 数据库连接 | 查询错题列表接口 | 正常返回数据 | 检查连接串、数据库状态 |
| 3 | gRPC 端口可达 | portal 调用 mistake 接口 | 正常响应 | 检查 gRPC 端口、防火墙规则 |
| 4 | OSS 读写正常 | 审核完成后检查图片迁移 | `mistakes/` 前缀下存在图片 | 检查 OSS 凭证、桶权限、磁盘空间 |
| 5 | 数据库迁移完整 | 检查 `__EFMigrationsHistory` | 存在 `20260514051544_InitialCreate` 记录 | 手动 stamp 或排查迁移日志 |
| 6 | 旧表已迁移 | 检查 `mistake` 表是否存在 | 不存在（已重命名为 `mistake_item`） | 如仍存在，检查迁移日志是否跳过 |
| 7 | 列名已更新 | 检查 `process_log` 和 `mistake_practice` 表列名 | 使用 `mistake_item_id` 而非 `mistake_id` | 手动执行列重命名 SQL |
| 8 | 外键约束存在 | 检查 `process_log` 和 `mistake_practice` 的外键 | 存在指向 `mistake_item(id)` 的外键 | 手动添加外键约束 |
| 9 | 索引已创建 | 检查 `mistake_item` 表索引 | 存在 5 个业务索引 | 手动创建缺失索引 |
| 10 | gRPC 消息大小 | 上传含大量图片的错题 | 不超过 100MB 限制 | 如需更大限制，修改 `Program.cs` 中的消息大小配置 |
| 11 | SQLite 文件位置（如适用） | 检查 `data/sqlite/ruoyu_study_mistake.db` | 文件存在且可读写 | 检查目录权限 |
