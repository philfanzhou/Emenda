# ruoyu.mistake 部署集成说明

本文档描述 `ruoyu.mistake` 服务的完整部署流程，包括配置准备、数据库初始化、OSS 准备、依赖服务协调、启动验证等环节。

---

## Consul 接入约束

`ruoyu.mistake` 固定接入 Consul，通过 `ruoyu.common` 下的共享 Consul 代码读取共享配置。Mistake 不向 Consul 注册服务实例；调用方直接读取 `MistakeService:Url`。

共享 Consul 配置承载：

- `config/ruoyu/shared.json`
  - `PostgreSql:Host`
  - `PostgreSql:Port`
  - `PostgreSql:Username`
  - `PostgreSql:Password`
  - `Oss:*`
  - `Loki:Uri`（Loki 日志地址；跨主机示例为 `http://192.168.100.10:3100`，由 `Program.cs` 读取并覆盖 Serilog Loki Sink 的 uri）
- `config/ruoyu/service-endpoints.json`
  - `StudentService:Url`（Student 服务 HTTP 地址；跨主机示例为 `http://192.168.100.10:5005`）

服务私有配置仍保留在 Mistake 自身：

- `Database:Name=ruoyu_study_mistake`
- `VlAnalyzer:*`

> **HTTP 监听端口固定为 5007**：硬编码在 `Program.cs` 的 `ConfigureKestrel` 中，不通过配置键 `Endpoints:Http` 读取，启动脚本也不再注入 `Endpoints__Http` 环境变量。host 端口映射通过 `start.sh` 的 `Port` 变量控制（`-p ${Port}:5007`）。

仓库启动脚本面向局域网独立宿主机部署：不创建或加入 `ruoyu-net`，通过 `-p 5007:5007` 发布服务。`CONSUL_HTTP_ADDR` 的仓库示例为 `192.168.100.10:8500`，部署时必须替换为实际可达地址；Portal 通过 Consul `MistakeService:Url` 访问发布后的 host 端口。

启动脚本 `start.sh` 只保留：

- `CONSUL_HTTP_ADDR`
- `CONSUL_TOKEN`
- `Database__Name`
- `VlAnalyzer__ApiKey`
- `VlAnalyzer__BaseUrl`
- `VlAnalyzer__Model`

> 协议策略遵循 [ADR-001](../overview/ADR-001-mistake-webapi-only.md)：WebAPI-Only，gRPC 端口 5006 已于 2026-07-12 下线。

---

## 端口与协议说明

| 端口 | 协议 | 用途 | 配置方式 |
|------|------|------|---------|
| HTTP 端口（容器内固定） | HTTP/1 | 全部 18 个业务端点 + 健康检查（`GET /`） | `Program.cs` 硬编码 5007，不可配置 |
| host 映射端口 | — | host 访问容器服务的映射端口 | `start.sh` 的 `Port` 变量（`-p ${Port}:5007`） |

**HTTP 端口**承载全部业务接口（18 个端点，路由前缀 `/api/mistakes`），包括 `SubmitMistakeUpload`、`GetMistakeItem`、`ReviewMistakeItem`、`CompleteUploadReview` 等。同时 `GET /` 返回服务状态字符串用于健康检查。

> gRPC 端口 5006 已于 2026-07-12 下线（见 [ADR-001](../overview/ADR-001-mistake-webapi-only.md)）。

---

## 流程一：配置准备

| 项目 | 内容 |
|------|------|
| **节点名称** | 配置准备 |
| **前置条件** | 已获取部署环境信息（Consul 地址、ACL token、VL 分析私有配置） |
| **输入条件** | appsettings.json 模板、环境变量清单 |
| **操作步骤** | 1. 配置 `CONSUL_HTTP_ADDR` 和 `CONSUL_TOKEN`<br>2. 确认 Consul 中已存在 `PostgreSql:*`、`Oss:*`、`StudentService:Url`<br>3. 在 Mistake 自身配置中保留 `VlAnalyzer:*`、`Database:Name`<br>4. HTTP 监听端口固定为 5007（`Program.cs` 硬编码），如需 host 端口映射，设置 `start.sh` 的 `Port` 变量 |
| **成功判定** | 所有必填配置项均有有效值；host 映射端口不与已有服务冲突 |
| **失败后排查方向** | 1. 配置键拼写错误 → 对照 appsettings.json 结构检查<br>2. 端口冲突 → `netstat` 检查端口占用<br>3. 环境变量未生效 → 确认运行环境是否正确注入 |

### 配置项速查

```json
{
  "Consul": {
    "Host": "192.168.100.10",
    "Port": 8500,
    "KvPrefix": "config/ruoyu"
  },
  "Database": {
    "Name": "ruoyu_study_mistake"
  },
  "VlAnalyzer": {
    "BaseUrl": "https://api.stepfun.com/v1",
    "Model": "step-3.7-flash"
  },
  "Loki": {
    "Uri": "http://192.168.100.10:3100"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "GrafanaLoki",
        "Args": {
          "uri": "http://192.168.100.10:3100",
          "labels": [
            { "key": "service", "value": "Ruoyu.Study.Mistake" }
          ]
        }
      }
    ]
  }
}
```

### 数据库类型判定逻辑

连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 构建：优先从 Consul 共享配置（`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`）合成 PostgreSQL 连接串，无法合成时回退到本地 `ConnectionStrings:Default`（必须为 PostgreSQL 连接串）。

### OSS 模式判定逻辑

| 环境变量 | OSS 实现 | 存储路径 |
|----------|---------|---------|
| `USE_LOCAL_OSS=1` | `LocalFileOssService` | `OSS_LOCAL_PATH` 环境变量指定，默认 `data/oss` |
| 未设置或非 `1` | `S3OssService` | 由 `Oss:InternalEndpoint`、`Oss:InternalSecure` 和 `Oss:BucketName` 决定 |

---

## 流程二：数据库准备

1. 停止全部 Mistake 应用写入，取得目标 PostgreSQL 数据库的可恢复备份，在隔离测试库按[备份恢复演练](./BackupRestoreDrill.md)恢复并核对结构、行数、稳定 ID、关键引用与内容。只有完成恢复校验后才可确认 `--backup-verified`。确认连接配置的数据库名、主机和环境，准备有目标表 DDL 与迁移历史写入权限的维护账号；普通启动账号仅需只读权限。备份文件和连接凭据不得写入仓库。
2. 对**全新空库**，使用与 Host 相同配置运行 `dotnet Ruoyu.Study.Mistake.Host.dll --upgrade-empty-db`（或在容器内以该参数运行 Host）。命令完成且打印 `Mistake empty database upgrade verified.` 后，检查四条 `__EFMigrationsHistory` 记录和 `mistake_item`、`mistake_practice`、`process_log`、`reanalyze_job` 四张表，再正常启动服务。
3. 对受支持的带数据旧库，在停写与可恢复备份核验完成后，使用与 Host 相同配置运行 `dotnet Ruoyu.Study.Mistake.Host.dll --upgrade-known-db --database <目标数据库名> --writes-stopped --backup-verified`。`--database` 必须与连接配置中的数据库名完全相同；两个确认开关由操作者在完成对应操作后提供。命令在同库事务与 advisory lock 内核验精确 EF 历史结构，只迁移缺失版本，并核验四历史目标后提交。输出 `Mistake known database upgrade verified.` 后，再以只读账号正常启动并核查业务条目、练习路径、日志和历史记录。相同目标可重复运行；并发命令串行复核。
4. 对已知运行时结构，即前三条 EF 迁移历史且 `mistake_item.root_cause` 已存在、`review_comment` 不存在，升级前也可正常启动。启动仅作只读 catalog/历史核验，不执行 DDL、stamp 或 `MigrateAsync`；显式带数据升级只在精确核验后补第四条历史。
5. 对已完成第四条迁移的目标结构，正常启动，同样只读核验。两个显式升级命令在该结构上可重复执行，无数据或结构变更。

`--upgrade-empty-db` 在事务中取得 PostgreSQL advisory lock，仅为空库创建目标结构；DDL 或核验失败时回滚。它不迁移已有数据。`--upgrade-known-db` 只接受精确的 1/2/3 条 EF 历史结构、已重命名的三历史结构及四历史目标。非法 JSON、额外/缺少对象、`public.mistake` 旧表、只有历史表、未知或矛盾历史都会拒绝，事务在提交前失败时回滚。异常或取消后先保持停写，重新以只读方式核对 catalog、历史与业务行，再重试显式命令；若提交时连接中断，结果可能未知，必须重新连接核验，不据错误消息猜测是否已提交。若升级后的业务数据或行为不正确，停止写入，按已验证备份流程恢复至新测试库并核对后再决定实际恢复；不把 EF Down 当作生产恢复保证。已支持 EF schema 的隔离演练见[备份恢复演练](./BackupRestoreDrill.md)；旧表映射与误盖章修复仍属 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK)。勿手工 stamp 或删表绕过检查。本说明不表示已对生产库执行升级。

普通启动账号至少需要读取 public schema 的 catalog、四张业务表和 `__EFMigrationsHistory`；只读数据库连接可完成启动核验。执行显式升级的账号另需目标对象的 DDL 与迁移历史写入权限。连接失败或权限不足会中止对应操作。

详见[迁移历史](../database/migrations.md)及[故障排查](./Troubleshooting.md)。

---

## 流程三：OSS 准备

| 项目 | 内容 |
|------|------|
| **节点名称** | OSS 准备 |
| **前置条件** | 配置准备完成 |
| **输入条件** | S3 兼容存储服务已部署（生产）或本地文件系统可写（开发） |
| **操作步骤** | 1. 生产模式：确认 MinIO/SeaweedFS 已运行，`Oss:BucketName` 指定的桶已创建<br>2. 开发模式：设置 `USE_LOCAL_OSS=1`，确认 `data/oss` 目录可写<br>3. 验证凭证有效性（生产模式） |
| **成功判定** | 服务启动后，涉及 OSS 的操作（如 `CompleteUploadReview`）可正常执行 |
| **失败后排查方向** | 1. S3 连接失败 → 检查 `Oss:InternalEndpoint` 是否可达、`InternalSecure` 与协议是否匹配、AccessKey/SecretKey 是否正确<br>2. Bucket 不存在 → 手动创建或确认自动创建策略<br>3. 本地 OSS 路径不可写 → 检查目录权限 |

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
| **输入条件** | mistake 服务的 HTTP 地址 |
| **操作步骤** | 1. 确认 mistake 服务已启动并在 HTTP 端口（默认 5007）监听<br>2. 确认各 portal 的 mistake 地址配置正确 |
| **成功判定** | 各 portal 可正常调用 mistake 的 HTTP 接口 |
| **失败后排查方向** | 1. portal 地址不可达 → 检查地址配置、网络连通性<br>2. HTTP 调用超时 → 检查 mistake 是否在 5007 端口监听<br>3. 调用返回错误 → 查看 portal 日志中的 HTTP 错误详情 |

### 各 portal 的 mistake 地址配置

| Portal | 配置键 | 仓库部署示例 |
|--------|--------|--------------|
| Ruoyu.Admin | `MistakeService:Url` | `http://192.168.100.10:5007` |
| teacher_portal | `MistakeService:Url` | `http://192.168.100.10:5007` |
| user_portal | `MistakeService:Url` | `http://192.168.100.10:5007` |
| assistant_portal | `MistakeService:Url` | `http://192.168.100.10:5007` |

实际地址只在 Consul `config/ruoyu/service-endpoints.json` 中维护。修改 live KV 后，需要重启读取该配置的 Portal API；如果 API 容器被重建，对应前端 Nginx 也应重启以重新解析 Docker 上游地址。

---

## 流程五：服务启动

| 项目 | 内容 |
|------|------|
| **节点名称** | 服务启动 |
| **前置条件** | 配置准备、数据库准备、OSS 准备、依赖服务准备均已完成 |
| **输入条件** | 编译产物、appsettings.json、环境变量 |
| **操作步骤** | 1. 确认镜像 `ruoyu.study.mistake:20260501` 已构建<br>2. 设置实际 `CONSUL_HTTP_ADDR`、`CONSUL_TOKEN` 和 VL 私有配置<br>3. 执行 `bash start.sh`<br>4. 观察启动日志 |
| **成功判定** | 日志中出现监听端口信息，无异常；数据库只读核验日志正常 |
| **失败后排查方向** | 1. 端口绑定失败 → 检查端口占用<br>2. 数据库连接失败 → 检查连接串和数据库状态<br>3. 依赖注入失败 → 检查配置项完整性 |

### 启动顺序

```
1. PostgreSQL
2. OSS 服务（MinIO/SeaweedFS）或确认本地目录可写
3. student 服务（不强制先于 mistake，但建议先启动）
4. mistake 服务
5. 各 portal 服务
```

---

## 流程六：健康检查

| 项目 | 内容 |
|------|------|
| **节点名称** | 健康检查 |
| **前置条件** | 服务已启动 |
| **输入条件** | HTTP 端口可访问 |
| **操作步骤** | 1. 向 HTTP 端口发送 `GET /` 请求<br>2. 检查响应状态码和内容 |
| **成功判定** | HTTP 200，响应体为 `"Mistake WebAPI host is running."` |
| **失败后排查方向** | 1. 无响应 → 服务未启动或 HTTP 端口配置错误<br>2. 连接拒绝 → 端口未监听，检查 Kestrel 配置<br>3. 返回非预期内容 → 确认请求的是 HTTP 端口 5007 |

### 健康检查方式

```bash
curl http://<host>:5007/
```

预期返回：`Mistake WebAPI host is running.`

> **注意**：健康检查走 HTTP 端口（默认 5007），承载全部 18 个业务端点。mistake→student 的调用也通过 HTTP（见 [ADR-001](../overview/ADR-001-mistake-webapi-only.md)）。

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
| 1 | student → mistake 连通 | student 端触发上传，观察 mistake 日志是否收到 `SubmitMistakeUpload` | 检查 student 的 mistake 地址配置、HTTP 端口是否正确 |
| 2 | portal → mistake 连通 | 通过 portal 页面查询错题列表，观察是否返回数据 | 检查 portal 的 mistake 地址配置 |
| 3 | mistake → OSS 操作 | 执行 `CompleteUploadReview`，确认图片从 `uploads/` 迁移到 `mistakes/` | 检查 OSS 配置和桶权限 |
| 4 | Ruoyu.Admin OSS 审计 | 触发 `GetAllReferencedImagePaths`，确认返回图片路径列表 | 检查 Ruoyu.Admin 的 mistake 地址配置 |

### 推荐联调顺序

```
student → mistake（SubmitMistakeUpload）
    ↓
teacher_portal → mistake（GetPendingReviewUploads → ReviewMistakeItem）
    ↓
teacher_portal → mistake → OSS（CompleteUploadReview）
    ↓
Ruoyu.Admin → mistake（GetMistakeItemList、GetAllReferencedImagePaths）
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
| 3 | HTTP 端口可达 | portal 调用 mistake 接口 | 正常响应 | 检查 HTTP 端口 5007、防火墙规则 |
| 4 | OSS 读写正常 | 审核完成后检查图片迁移 | `mistakes/` 前缀下存在图片 | 检查 OSS 凭证、桶权限、磁盘空间 |
| 5 | 数据库迁移完整 | 检查 `__EFMigrationsHistory` 和实际 schema | 历史与表结构一致 | 保留备份，核验迁移日志与 schema，不手动 stamp |
| 6 | 旧表保护 | 检查 `public.mistake` 是否存在 | 不存在；存在时服务拒绝启动 | 保留数据库及备份，走显式升级流程，不使用旧版本绕过 |
| 7 | 列名已更新 | 检查 `process_log` 和 `mistake_practice` 表列名 | 使用 `mistake_item_id` 而非 `mistake_id` | 停止写入，保留备份与诊断，交 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 的旧库升级切片处理；不手工改列 |
| 8 | 外键约束存在 | 检查 `process_log` 和 `mistake_practice` 的外键 | 存在指向 `mistake_item(id)` 的外键 | 停止写入，保留备份与诊断，交 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 的旧库升级切片处理；不手工补外键 |
| 9 | 索引已创建 | 检查 `mistake_item` 表索引 | 存在 5 个业务索引 | 停止写入，保留备份与诊断，交 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 的旧库升级切片处理；不手工补索引 |
| 10 | HTTP 请求大小 | 上传含大量图片的错题 | 不超过服务器请求体大小限制 | 如需更大限制，修改 Kestrel `MaxRequestBodySize` |
