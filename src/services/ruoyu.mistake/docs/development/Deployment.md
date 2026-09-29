# ruoyu.mistake 部署集成说明

本文档描述 `ruoyu.mistake` 服务的完整部署流程，包括配置准备、数据库初始化、OSS 准备、依赖服务协调、启动验证等环节。

---

## 配置引导模式

默认和显式 `Mistake:ConfigMode=Platform` 沿用 Consul→缓存→本地回退与 Console+Loki。Mistake 通过 `ruoyu.common` 下的共享 Consul 代码读取共享配置；不向 Consul 注册服务实例，调用方直接读取 `MistakeService:Url`。`Mistake:ConfigMode=Local` 在加载 Consul 前选择，仅使用标准 `appsettings*.json`/环境配置，不请求 Consul、不读取其缓存，日志仅写 Console。仅接受 `Local` 和 `Platform`（大小写不敏感）；非法值在外部连接前失败。此切片只隔离配置和日志引导，**不代表**无 Student/认证/OSS/AI 时业务能力已可安全独立运行；这些仍按现有依赖工作。

Local 必须显式提供 `ConnectionStrings:MistakeLocal`（环境键 `ConnectionStrings__MistakeLocal`），仓库默认留空。不得使用 `PostgreSql:*`、Consul 缓存或示例 `ConnectionStrings:Default` 作为 Local 连接；缺失、无效或与平台示例相同会在连接前失败。环境变量优先于文件；引导仅记录 `Mode` 和数据库连接的 `Environment`/`File` 来源，不输出连接串。选择 Local 本身不授权 DDL；两个显式升级命令仍受原数据库指纹、停写、备份和库名门禁约束。凭据应由部署者通过受保护的环境或挂载文件提供。

在仅供丢弃的隔离 PostgreSQL 15 测试库中，可按下列方式验证引导；`<...>` 必须替换为测试环境值，不要把凭据写入仓库：

```bash
export Mistake__ConfigMode=Local
export ConnectionStrings__MistakeLocal='Host=<test-host>;Port=5432;Database=<test-database>;Username=<test-user>;Password=<test-password>'
dotnet Ruoyu.Study.Mistake.Host.dll --upgrade-empty-db
dotnet Ruoyu.Study.Mistake.Host.dll
```

正常启动对数据库只读核验，空库须先显式升级。带数据且精确受支持的旧库应按下文备份恢复演练后使用 `--upgrade-known-db --database <test-database> --writes-stopped --backup-verified`。Local 模式缺连接时会报告 `Local mode requires ConnectionStrings:MistakeLocal`；模式拼写错误会报告允许值；连接失败时检查来源和目标库而不打印密码。默认 Platform 的 Consul/缓存诊断与 Loki URI 覆盖保持原样。

### 后台 worker 启动开关

Mistake Host 在启动时为三个 worker 分别读取以下布尔配置。缺失时 Platform（包括未显式设置模式）默认为 `true`，Local 默认为 `false`。文件中的 `true` / `false` 可由同名双下划线环境变量逐项覆盖；其他值在数据库初始化和 worker 启动前报出具体键并拒绝启动。Platform 先加载 Consul，再校验最终值，因此非法值不保证阻止 Consul 访问。

| 文件配置键 | 环境变量 | 控制的启动行为 |
|---|---|---|
| `Mistake:Workers:UploadPollingWorker:Enabled` | `Mistake__Workers__UploadPollingWorker__Enabled` | Student 轮询采集 |
| `Mistake:Workers:ReanalyzeJobHostedService:Enabled` | `Mistake__Workers__ReanalyzeJobHostedService__Enabled` | 重分析任务领取、续租及过期重领 |
| `Mistake:Workers:ReanalyzeJobCleanupService:Enabled` | `Mistake__Workers__ReanalyzeJobCleanupService__Enabled` | 终态任务 30 天清理 |

例如在 `appsettings.Production.json` 中只关闭采集：

```json
{
  "Mistake": {
    "Workers": {
      "UploadPollingWorker": { "Enabled": false }
    }
  }
}
```

也可设置 `Mistake__Workers__UploadPollingWorker__Enabled=false` 覆盖文件值。启动日志只打印各 worker 的最终 `Enabled` 状态；值在进程启动时固定，修改文件、环境或 Consul 配置后必须重启。关闭某 worker 后，本进程不会启动其轮询、恢复或清理；数据库中已有 Pending/Running、终态与对象引用不会因关闭而被改写。重新启用时只恢复原 worker 行为，不重置任务状态；重分析 worker 只重领租约已过期的 Job，不改写其他实例的有效 Running；达到三次领取上限后明确失败。

开关只作用于本进程。运维须核对所有实例的最终开关以及停机时的在途请求和任务；其他实例可能继续处理，在途外部请求也不会回滚。回退到旧二进制前应重新核对三项状态：旧版即使在 Local 模式也会无条件启动三个 worker，降级本身不能用作安全停机。禁用采集不禁用手动上传或退回 HTTP 入口；Local 模式仍不能因此匿名对公网开放，完整能力与身份边界由后续任务处理。

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

HTTP/1 监听端口由 `Mistake:Http:Port`（环境键 `Mistake__Http__Port`）控制；缺失时 Local、默认及显式 Platform 都使用 5007。显式值必须是 1–65535 的整数；空白、非整数或越界值会在数据库连接与监听前以键名报错，不回退默认端口。Local 读取文件与环境配置，环境值覆盖文件值；Platform 在加载 Consul 后读取最终配置。`Endpoints:Http` 与 `ASPNETCORE_URLS` 不决定监听端口。启动日志只记录最终端口，变更配置须重启进程。

默认 Docker `EXPOSE 5007` 和 `start.sh` 的 `-p ${Port}:5007` 保持兼容。直接运行可设置 `Mistake__Http__Port`；容器内改用其他端口时，部署者还须调整 `-p host:container`、网络规则及各消费方的 `MistakeService:Url`。回退时移除该键、重启并将映射及调用方地址同步回 5007。端口被占用时启动失败，不会自动改用 5007 或其他端口；不要将目前的匿名业务 API 直接暴露公网。

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
| HTTP 端口（容器内默认 5007） | HTTP/1 | 全部 18 个业务端点 + 健康检查（`GET /`） | `Mistake:Http:Port`，环境键 `Mistake__Http__Port` |
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
| **操作步骤** | 1. 配置 `CONSUL_HTTP_ADDR` 和 `CONSUL_TOKEN`<br>2. 确认 Consul 中已存在 `PostgreSql:*`、`Oss:*`、`StudentService:Url`<br>3. 在 Mistake 自身配置中保留 `VlAnalyzer:*`、`Database:Name`<br>4. HTTP 监听默认 5007；如需覆盖，配置 `Mistake:Http:Port` 并同步容器映射及消费方地址 |
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

Platform 连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 构建：优先从 Consul 共享配置（`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`）合成 PostgreSQL 连接串，无法合成时回退到本地 `ConnectionStrings:Default`（必须为 PostgreSQL 连接串）。Local 只读取显式 `ConnectionStrings:MistakeLocal`，环境值覆盖文件值。

### OSS 模式判定逻辑

| 环境变量 | OSS 实现 | 存储路径 |
|----------|---------|---------|
| `USE_LOCAL_OSS=1` | `LocalFileOssService` | `OSS_LOCAL_PATH` 环境变量指定，默认 `data/oss` |
| 未设置或非 `1` | `S3OssService` | 由 `Oss:InternalEndpoint`、`Oss:InternalSecure` 和 `Oss:BucketName` 决定 |

---

## 流程二：数据库准备与重分析切换

1. **A 停写备份**：逐一关闭所有实例的 `Mistake__Workers__ReanalyzeJobHostedService__Enabled`，等待或终止在途 VL；再停止 Mistake 写入。确认准确主机与数据库名，取得可恢复备份，并在隔离 PostgreSQL 15 库恢复核对结构、行数、稳定 ID、Job 状态与 ProcessLog。只有核验后才使用 `--backup-verified`。不可让旧 worker 与新 worker 混跑。
2. **B 显式升级**：全新空库运行 `dotnet Ruoyu.Study.Mistake.Host.dll --upgrade-empty-db`；受支持带数据历史库运行 `dotnet Ruoyu.Study.Mistake.Host.dll --upgrade-known-db --database <目标数据库名> --writes-stopped --backup-verified`。维护账号需 DDL 与迁移历史写权限。命令在单一事务和 advisory lock 内升级至**五条** EF 历史并核验精确 catalog；第四条历史目标升级到第五条。旧无租约 Running 被置为 `Failed/upgrade_interrupted`，Item 存在时补一条关联日志；Pending、已有终态和 ID 保留。未知或矛盾结构原状拒绝，旧 `public.mistake` 由 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 处理。
3. **C 验库启用**：用新二进制执行正常启动，只读核验精确五历史目标，然后逐一启用重分析 worker。核查 Running 的 owner/version/lease、全局有效 Running ≤ 2、过期任务可重领、同题后继等待。正常启动不迁移数据库。旧二进制遇新 schema 应失败关闭。
4. **D 撤回**：先关闭所有实例的重分析 worker，保留队列和 lease 诊断。修正新二进制后可重新启用，过期 Job 自动重领；若确需旧二进制，停止写入，在隔离库恢复已核验的升级前备份并核对，再按恢复流程切换。单纯关开关、直接运行旧二进制或 EF Down 均不是无损回退。

提交前故障会使升级事务回滚。COMMIT 响应丢失时结果未知，保持停写并重新只读核对 catalog、五条历史和业务行，再决定是否重复运行显式命令；不得凭异常文本猜测结果。重复升级到精确目标只核验，不增加失败日志。具体结构和支持的旧形态见[迁移历史](../database/migrations.md)，隔离备份演练见[备份恢复演练](./BackupRestoreDrill.md)。本手册不表示已执行生产升级。

普通启动账号需要读取 public catalog、四张业务表和迁移历史；worker 账号需要 Job/Item/ProcessLog 写权限。数据库或租约异常时，先关闭所有重分析 worker，检查 `owner_id`、`fencing_version`、`lease_until`、`attempt` 及关联日志，不手工改 owner/version 或清空队列。参见[故障排查](./Troubleshooting.md)。

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
