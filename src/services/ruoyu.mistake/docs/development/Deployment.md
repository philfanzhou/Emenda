# ruoyu.mistake 部署集成说明

本文档描述 `ruoyu.mistake` 服务的完整部署流程，包括配置准备、数据库初始化、OSS 准备、依赖服务协调、启动验证等环节。

---

## 配置引导模式

默认和显式 `Mistake:ConfigMode=Platform` 沿用 Consul→缓存→本地回退与 Console+Loki。Mistake 通过 `ruoyu.common` 下的共享 Consul 代码读取共享配置；不向 Consul 注册服务实例，调用方直接读取 `MistakeService:Url`。`Mistake:ConfigMode=Local` 在加载 Consul 前选择，仅使用标准 `appsettings*.json`/环境配置，不请求 Consul、不读取其缓存，日志仅写 Console。仅接受 `Local` 和 `Platform`（大小写不敏感）；非法值在外部连接前失败。此切片只隔离配置和日志引导，**不代表**无 Student/认证/OSS/AI 时业务能力已可安全独立运行；这些仍按现有依赖工作。

Local 必须显式提供 `ConnectionStrings:MistakeLocal`（环境键 `ConnectionStrings__MistakeLocal`），仓库默认留空。不得使用 `PostgreSql:*`、Consul 缓存或示例 `ConnectionStrings:Default` 作为 Local 连接；缺失、无效或与平台示例相同会在连接前失败。环境变量优先于文件；引导仅记录 `Mode` 和数据库连接的 `Environment`/`File` 来源，不输出连接串。两种模式启动时都按[迁移历史](../database/migrations.md)自动升级受支持的数据库结构，不支持的结构拒绝启动且不修改。凭据应由部署者通过受保护的环境或挂载文件提供。

Local 正常运行还必须显式提供独立的 `Mistake:Local:Oss:*` 六项配置（环境键前缀 `Mistake__Local__Oss__`），包括内部 S3 endpoint、`InternalSecure`、桶、凭据和公共预签名基础 URL。它不会从通用 `Oss:*` 或仓库 mock 配置回退。文件/环境占位示例、公开代理 Host 与路径要求、故障排查和回退见 [Local OSS 配置](../Integration/Oss/LocalConfiguration.md)。两个兼容升级命令（`--upgrade-empty-db`、`--upgrade-known-db`）不要求这些 OSS 键。

在仅供丢弃的隔离 PostgreSQL 15 测试库中，可按下列方式验证引导；`<...>` 必须替换为测试环境值，不要把凭据写入仓库：

```bash
export Mistake__ConfigMode=Local
export ConnectionStrings__MistakeLocal='Host=<test-host>;Port=5432;Database=<test-database>;Username=<test-user>;Password=<test-password>'
export Mistake__Local__Oss__InternalEndpoint='<s3-host>:<s3-port>'
export Mistake__Local__Oss__InternalSecure=false
export Mistake__Local__Oss__AccessKey='<access-key>'
export Mistake__Local__Oss__SecretKey='<secret-key>'
export Mistake__Local__Oss__BucketName='<existing-bucket>'
export Mistake__Local__Oss__PublicBaseUrl='https://<public-host>/oss'
dotnet Ruoyu.Study.Mistake.Host.dll
```

正常启动时自动把空库和受支持的旧库升级到当前结构，已是目标时不写入。Local 模式缺连接时会报告 `Local mode requires ConnectionStrings:MistakeLocal`；模式拼写错误会报告允许值；连接失败时检查来源和目标库而不打印密码。默认 Platform 的 Consul/缓存诊断与 Loki URI 覆盖保持原样。

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

开关只作用于本进程。运维须核对所有实例的最终开关以及停机时的在途请求和任务；其他实例可能继续处理，在途外部请求也不会回滚。回退到旧二进制前应重新核对三项状态：旧版即使在 Local 模式也会无条件启动三个 worker，降级本身不能用作安全停机。禁用采集 worker 本身不禁用手动上传 HTTP 入口；Student 能力禁用会使退回审核失败关闭（见下节）。Local 模式仍不能因此匿名对公网开放，完整能力与身份边界由后续任务处理。

### Student 能力开关与退回失败关闭

`Mistake:Student:Enabled`（环境键 `Mistake__Student__Enabled`）控制本进程的 Student 集成能力（采集轮询与退回审核的 Student 调用）。缺省值按模式区分：Local 为 `false`（未接入 Student 时失败关闭），Platform 为 `true`（保持既有启用行为）。显式值只接受 `true`/`false`；其他值在数据库初始化、worker 启动和任何外部连接前以键名报错并拒绝启动。

| 配置键 | 环境变量 | 作用 |
|---|---|---|
| `Mistake:Student:Enabled` | `Mistake__Student__Enabled` | Student 集成能力总开关（Local 缺省 `false`，Platform 缺省 `true`） |
| `Mistake:Local:Student:Url` | `Mistake__Local__Student__Url` | Local 启用能力时的 Student 显式 HTTP(S) 基础地址，必填 |

规则与失败关闭语义：

- `Mistake:Student:Enabled=false` 与 `Mistake:Workers:UploadPollingWorker:Enabled=true` 是矛盾配置：轮询 worker 依赖 Student，启动前以两个键名报错。Platform 显式禁用同样适用。
- Local `Enabled=true` 必须显式提供 `Mistake:Local:Student:Url`（绝对 HTTP(S) 地址，不含凭据、路径、查询或片段）。缺失、空白或无效时在任何数据库连接前报键名失败；`StudentService:Url`/`BaseUrl`/`GrpcUrl` 与仓库示例 `http://localhost:5005` 不会作为回退补齐。Platform 保持既有 `StudentService:Url` → `BaseUrl` → `GrpcUrl` → `http://localhost:5005` 地址兼容，不因本切片改动。
- 能力禁用时：即使配置文件仍留有 Student 地址，本进程也不会向 Student 发出任何请求；`UploadPollingWorker` 不得启用；`POST /api/mistakes/{id}/review` 携带 `ReviewStatus=Rejected` 时在调用领域服务、OSS 或 Student 之前返回 HTTP 503 与稳定响应 `{"success":false,"message":"Student 集成未启用，退回审核不可用（Mistake:Student:Enabled=false）"}`，不更新审核状态、不复制/删除 OSS 对象、不删除错题条目、不创建 ReturnedRecord，重复请求结果一致。`ReviewStatus=Confirmed` 路径与既有本域读 API 不受能力关闭影响。
- 禁用态仍保持 Host 正常依赖注入启动；共享 Student 客户端注册到保留的不可解析占位地址，任何意外调用都会立即网络失败而不是触达真实服务。启动诊断只记录 `Mode`、`Enabled`、地址来源（`Explicit`/`Platform`/`Disabled`），不输出凭据。
- 开关在进程启动时固定；重新启用或改地址必须重启并重新通过上述校验。与 Local OSS 键一致，两个兼容升级命令不要求 `Mistake:Local:Student:Url`。

兼容窗口与既有行为：启用 Student 后的失败吞没、响应丢失、重复 ReturnedRecord、跨 DB/OSS 原子性与授权仍由 [IKIZY1](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY1)、[IKIZY3](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY3)、[IKIZY0](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY0) 处理；本开关只阻止"未接入 Student"模式下的明显数据丢失路径。

### 入站 API 认证（兼容窗口，默认关闭）

Mistake Host 通过共享 `Ruoyu.Study.Common.Authentication` 接入 JWT Bearer 验证（ADR-0008 的 2026-09-30 Mistake 修订），全部 18 个 `/api/*` 业务端点默认处于**匿名兼容窗口**：行为与历史版本一致，`/` 与 `/health/*` 健康端点始终匿名。

| 配置键 | 环境变量 | 作用 |
|---|---|---|
| `IdentityService:MistakeApiStrictAuth` | `IdentityService__MistakeApiStrictAuth` | 收紧开关（默认 `false`，启动快照）：`true` 时 `/api/*` 要求已认证主体（共享受众或 Admin AppId 受众令牌），匿名与无效令牌一律 401 |
| `IdentityService:Authority` / `Issuer` / `Audience` | `IdentityService__Authority` 等 | 信任快照；strict=true 时三项必填，缺失在数据库访问前以键名显式启动失败 |
| `IdentityService:AdminAudience` | `IdentityService__AdminAudience` | 可选 Admin AppId 受众；未配置时该受众失败关闭，共享受众路径不受影响 |

规则与边界：

- Platform 模式信任快照来自共享 Consul `IdentityService` 配置节（与 Portal 相同来源），可由容器环境变量覆盖；Local 模式不强制配置——完全未配置 `IdentityService` 时正常启动、不校验、不访问 JWKS（无 SignaCore 依赖也能运行）。
- strict=false（默认）时有效令牌仍会解析进 `HttpContext.User`（供后续逐资源授权切片使用），令牌验证失败视为匿名，不拒绝请求；不启用全局 fallback policy，根端点与后续健康端点保持匿名。
- 开关与信任配置都是启动快照：变更后必须重启进程。回滚 = 置 `false`（或移除键）重启，即恢复匿名窗口；无数据库、schema 或数据迁移。strict=true 的实际启用时点由父项 [IKIZY0](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY0) 在消费者升级与联测后决定，当前部署不应启用。
- 非法开关值（非 `true`/`false`）在数据库访问前报 `IdentityService:MistakeApiStrictAuth must be true or false.` 并拒绝启动。
- 调用方令牌透传：三个 Portal 经共享 `MistakeBff.HttpClients` 调用 Mistake 时自动透传当前请求的 Bearer（无令牌则不附加头），Portal 无需配置；Ruoyu.Admin 直连 Mistake 时需自行携带有效令牌。令牌只经内存请求管线，日志与诊断输出不包含令牌、签名 URL 或密钥。
- 本窗口只建立「调用方可信身份」，不做逐资源归属/角色授权（见下节）。

### 逐资源授权矩阵（IKJEPZ，默认关闭）

在 strict 认证之上，`IdentityService:MistakeApiResourceAuth`（环境键 `IdentityService__MistakeApiResourceAuth`，默认 `false`，启动快照）为全部 18 个 `/api/*` 端点实施学生归属 + 教职代管的逐资源授权（ADR-0013，PD-002 方案 A）。`resource=true` 要求 `MistakeApiStrictAuth=true`，否则启动失败（错误仅引用两个配置键）；非法值报 `IdentityService:MistakeApiResourceAuth must be true or false.`。

主体分类（role 声明，大小写不敏感，共享受众与 Admin AppId 受众同规则）：**Admin**（`admin`）、**Staff**（`teacher`/`assistant`）、**Learner-side**（其余已认证主体，含无 role 声明令牌）。Learner 允许学生集经 `IUploadSourceGateway` → Student `GetStudentsByIdentityAccountId(sub)` 按请求解析、不缓存。

| # | 端点 | Learner-side | Teacher/Assistant | Admin |
|---|---|---|---|---|
| 1 | POST /api/mistakes/upload | 403 | 403 | 允许（现状校验） |
| 2 | GET /api/mistakes/{id} | item.StudentId ∈ allowed 否则 403 | 允许 | 允许 |
| 3 | GET /api/mistakes/by-upload/{sourceUploadId} | 返回项 StudentId ⊆ allowed 否则 403 | 允许 | 允许 |
| 4 | GET /api/mistakes | studentId ∈ allowed；缺失注入 allowed 过滤（空集 403） | 允许（任意过滤） | 允许 |
| 5 | GET /api/mistakes/pending-reviews | 403 | 允许 | 允许 |
| 6 | POST /api/mistakes/{id}/review | 403 | reviewerId==sub 否则 403 | 同左 |
| 7 | POST /api/mistakes/{id}/reanalyze | 403 | reviewerId==sub 且请求 studentId==item.StudentId；任务持久化以 item.StudentId 为准 | 同左 |
| 8 | POST /api/mistakes/reanalyze/jobs | 403 | reviewerId==sub | 同左 |
| 9 | GET /api/mistakes/reanalyze/active | 403 | reviewerId==sub | 同左 |
| 10 | POST /api/mistakes | 403 | 允许（现状校验） | 允许 |
| 11 | PUT /api/mistakes/{id} | 403 | 403 | 允许；studentId 变更（owner 迁移）仅 Admin |
| 12 | DELETE /api/mistakes/{id} | item.StudentId ∈ allowed 否则 403 | 允许 | 允许 |
| 13 | POST /api/mistakes/complete-review（Obsolete） | 403 | reviewerId==sub | 同左 |
| 14 | POST /api/mistakes/{id}/practices | 归属且 practice.StudentId==item.StudentId | 403 | 403 |
| 15 | GET /api/mistakes/{id}/practices | item.StudentId ∈ allowed 否则 403 | 允许 | 允许 |
| 16 | POST /api/mistakes/{id}/process-logs | 归属且请求 StudentId==item.StudentId | 403 | 403 |
| 17 | GET /api/mistakes/{id}/process-logs | item.StudentId ∈ allowed 否则 403 | 允许 | 允许 |
| 18 | POST /api/mistakes/presigned-url | 对象路径被 allowed 学生条目 SourceRegions.SourceImagePath 或练习 ImagePaths 以 Ordinal 精确引用，否则 403 | `mistakes/` 前缀允许（审核可见性） | 同左 |

授权失败一律 HTTP 403（未认证 401 由 strict 层处理），不用 HTTP 200 + `success=false` 表达拒绝。失败关闭与能力表：

- `Mistake:Student:Enabled=false` 或归属解析调用失败/超时 → Learner-side 学生范围请求返回明确不可用（503，`学生归属解析不可用（Student 能力禁用或解析失败），学生范围访问已拒绝`），不空放行、不匿名放行；Staff/Admin 路径不解析归属、不受影响。
- 账号无关联学生（映射为空）→ 学生范围访问 403。
- 归属解析经共享 typed client 的严格变体（失败抛出，不吞成空列表）区分「调用失败」与「空映射」。
- 授权判定日志只记录主体 sub、端点、资源 ID 与结果；不含令牌与预签名 URL。

resource=false（默认）时 legacy 未受管端点与基线 `91fb253e` 行为逐字节一致，不额外启用本开关的授权判定；受管入库数据永久执行新能力的认证与归属保护。启用前置（Ruoyu.Admin 直连调用方 Bearer 升级、跨服务 E2E、回滚演练）属父项 [IKIZY0](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY0) 切片③；回滚 = 置 `false` 重启，无数据迁移。学科管辖裁决仍留 Portal BFF（`ISubjectResolver`）。真实管线矩阵验收测试位于 `src/ruoyu.mistake/src/Tests/Ruoyu.Study.Mistake.Service.Tests/MistakeApiResourceAuthTests.cs`，启动快照测试位于同目录 `MistakeApiResourceAuthStartupTests.cs`。

### 独立运行(Local)匿名边界与收紧路径

本节固化独立部署者视角的匿名面与收紧路径；配置键语义以上方[入站 API 认证](#入站-api-认证兼容窗口默认关闭)配置表为准，逐资源矩阵见 [ADR-0013](../../../../../docs/adr/0013-mistake-per-resource-authorization.md)，严格模式运行验证见 [IsolatedDeployVerification.md](./IsolatedDeployVerification.md) 的 `--strict-fixture` 阶段，此处不复制第二套规则。

**两模式匿名面（逐端点，与 master `27626bf2` 实际行为一致）**：

| 端点 | Local | Platform | strict=true 后 |
|---|---|---|---|
| `GET /`（兼容状态串） | 匿名 | 匿名 | 仍匿名 |
| `GET /health/live`、`GET /health/ready` | 匿名 | 匿名 | 仍匿名（位于 `/api` 组之外） |
| `GET /contracts/mistake-http-v1.json` | 匿名 | 匿名 | 仍匿名（机器合同独立于业务授权） |
| `/api/*` 18 个业务端点 + 3 个受管来源端点 | 匿名兼容窗口 | 匿名兼容窗口 | 未认证 401；resource=true 时再按矩阵 403/503 |

匿名兼容窗口语义（strict=false，两模式相同）：

- 有效令牌（共忍受众或 Admin AppId 受众）解析进 `HttpContext.User`，请求照常放行；
- 令牌验证失败（过期、伪造、错 issuer、错受众）视为匿名，**不拒绝请求**；
- 不启用全局 fallback policy；`/`、健康与合同端点不受窗口影响。
- 窗口是历史行为的兼容保留，**不是**已获批准的终态运行方式：父项 [IKJ0IL](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IL) 明确不开启「匿名全权限独立模式」，独立部署者不得据此把 Local 匿名面当作授权决定。

**收紧路径矩阵**：

| 项 | 语义 |
|---|---|
| `IdentityService:MistakeApiStrictAuth`（默认 `false`） | 启动快照；`true` 时 `/api/*` 要求已认证主体（双 scheme），匿名与无效令牌一律 401 |
| `IdentityService:MistakeApiResourceAuth`（默认 `false`） | 启动快照；`true` 前置要求 strict=true（否则启动失败，错误仅引用两个键）；启用后 18 端点执行 ADR-0013 矩阵 |
| strict=true 且信任配置缺失/非法 | 在数据库访问前以键名显式启动失败；strict=false 不校验——完全未配置 `IdentityService` 也能正常启动（Local 无 SignaCore 依赖可运行） |
| 回滚 | 置 `false`（或移除键）后重启即恢复匿名窗口；无数据库、schema 或数据迁移 |
| 下游能力开关依赖 | `Mistake:SourceIntake/SourceReturns/SourceHandoffs:Enabled=true` 要求 Student+strict+resource 同为 true；`Mistake:SourceTargets:Enabled=true` 要求 strict+resource；不满足在 DB/S3 初始化前失败 |

**启用时点与当前限制（截至 2026-10-08，如实记录）**：

- 当前部署**不应启用** strict。
- 消费者升级：Ruoyu.Admin 官方版本 `0.1.2`（2026-10-08 发布于 GHCR）已包含「服务端会话令牌作 Bearer 调用 Mistake」（`MistakeSessionHandler` 白名单转发）；此前的 `0.1.2-rc.2` 对 Mistake 不携带任何 Authorization，登录仍为密码授权。
- 真实环境：192.168.55.10 当前运行的 Ruoyu.Study 服务镜像 `20261008` 构建自 master `90e6334c`，已包含两开关代码（`/health/live`、`/health/ready` 返回 200）；Ruoyu.Admin 仍为 `0.1.2-rc.2`。Ruoyu.Study 自有镜像不受 55.10「GitHub 项目只用官方版本」规则约束，由本仓库自行构建部署。
- 演练前置（部署动作）：55.10 升级到 Ruoyu.Admin 官方 `0.1.2`、SignaCore `ADMIN` 应用 PerApplication 收敛。启用顺序、联测与回滚演练属 [IKIZY0](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY0) 切片③；本节不宣称已启用或已演练。

**独立部署者责任**：

- 匿名业务 API（`/api/*`）**不得暴露公网**；窗口期间的访问控制完全依赖网络边界。
- 启用 strict 前须为每个调用方备妥：受信 `IdentityService:Issuer`（及可选 `AdditionalValidIssuers`）、共忍受众 `Audience`、Admin 直连调用方的 `AdminAudience`（=其 AppId），以及经 `Authority` 的 JWKS 发现（HTTPS，或隔离 HTTP 环境显式 `RequireHttpsMetadata=false`）；issuer/受众/JWKS 与凭据的保管责任在部署者。
- strict 启用后，未携带有效令牌的调用方（含旧脚本与自定义集成）将收到 401——先完成调用方升级与跨服务联测再启用。

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
| HTTP 端口（容器内默认 5007） | HTTP/1 | 全部 19 个业务端点 + 健康检查（`GET /health/live`、`GET /health/ready`、兼容 `GET /`） | `Mistake:Http:Port`，环境键 `Mistake__Http__Port` |
| host 映射端口 | — | host 访问容器服务的映射端口 | `start.sh` 的 `Port` 变量（`-p ${Port}:5007`） |

**HTTP 端口**承载全部业务接口（19 个端点，路由前缀 `/api/mistakes`），包括 `SubmitMistakeUpload`、`GetMistakeItem`、`ReviewMistakeItem`、`CompleteUploadReview` 等。同时提供匿名健康端点：`GET /health/live`（纯存活）、`GET /health/ready`（按能力快照的就绪探测，见[流程六](#流程六健康检查)）与兼容保留的 `GET /` 状态字符串。

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

Mistake Host 无条件注册 `S3OssService`（直连 S3 兼容存储），不存在本地文件系统 OSS 分支；`USE_LOCAL_OSS` / `OSS_LOCAL_PATH` 是 Student Host 的开发用途开关，对 Mistake 无效。Mistake 的 OSS 配置来源只由配置引导模式决定：

| 配置模式 | OSS 配置来源 | 说明 |
|----------|---------|---------|
| `Mistake:ConfigMode=Local` | `Mistake:Local:Oss:*` 六项（环境键前缀 `Mistake__Local__Oss__`） | 六项全部必填，缺失或无效在任何数据库连接前失败；见 [Local OSS 配置](../Integration/Oss/LocalConfiguration.md) |
| 默认 / 显式 `Platform` | Consul 共享 `Oss:*` | `Oss:InternalEndpoint`、`Oss:InternalSecure`、`Oss:BucketName`、凭据与可选 `Oss:PublicBaseUrl` |

---

## 流程二：数据库准备与重分析切换

1. **A 停旧实例**：先停掉旧版本 Mistake 实例（单实例替换即可满足），不要让旧 worker 与新 worker 混跑。备份不是前置；需要可回退时，建议先取得可恢复备份并按[备份恢复演练](./BackupRestoreDrill.md)在隔离库核对。
2. **B 启动即升级**：用新版本正常启动。启动时在单一事务和 advisory lock 内把空库或已知 1–10 条 EF 历史的库升级到**十一条**历史并核验精确 catalog；三历史误盖章形态（含小写 `ix_process_log_student_id` 变体）在同一事务中标准化后继续迁移；2026-05-14 至 2026-09-29 版本自动维护留下的已知结构先收敛，被移除或改写的旧数据写入 `legacy_archive`，日志以 `Mistake legacy schema converged:` 给出归档条数和重置的非法练习路径条数。旧无租约 Running 被置为 `Failed/upgrade_interrupted`，Item 存在时补一条关联日志；Pending、已有终态和 ID 保留。已是目标时不写入。04-19 至 05-13 留下的精确 `public.mistake` 自动转换，旧条目与关系保留，同 ID 现条目优先，旧日志动作 3/4 改为 5/6，原值归档（见[迁移历史](../database/migrations.md#旧表-mistake-的转换)）；未知或矛盾结构拒绝启动且不修改。需要升级时数据库账号要有 DDL 与写迁移历史的权限。升级命令不受连接串 `Command Timeout` 限制，大库首次启动可能较久，部署的健康检查/启动等待时间需相应放宽，不要因等待超时反复杀进程重启（每次都会整体回滚重来）。
3. **C 启用 worker**：日志出现 `Mistake database schema verified at target version 11` 后，逐一启用重分析 worker。核查 Running 的 owner/version/lease、全局有效 Running ≤ 2、过期任务可重领、同题后继等待。旧版本镜像遇到新 schema：已是旧版本认识的目标时可以启动，否则失败关闭。
4. **D 撤回**：先关闭所有实例的重分析 worker，保留队列和 lease 诊断。修正新版本后可重新启用，过期 Job 自动重领；若确需回到更早的版本，停止写入，在隔离库恢复升级前备份并核对，再按恢复流程切换。单纯关开关、直接运行旧版本或 EF Down 均不是无损回退。已升级到十一条历史（含收敛过旧结构）的库，只认识十条迁移的版本会因历史不匹配拒绝启动；回退须使用包含 `20261008000001_AddLegacyArchive` 的版本，`legacy_archive` 非空时 Down 拒绝。

提交前任何故障（DDL 失败、取消、连接中断、权限不足）都使升级事务整体回滚，数据库保持升级前状态，启动失败。COMMIT 响应丢失时结果未知，再次启动会重新分类：已提交则直接按目标启动，未提交则重新升级；不得凭异常文本猜测结果。`--upgrade-empty-db` 与 `--upgrade-known-db` 仍可用于在启动前单独执行同一升级，`--writes-stopped`、`--backup-verified` 不再必需，显式 `--database` 与实际库名不一致时拒绝。具体结构和支持的旧形态见[迁移历史](../database/migrations.md)。本手册不表示已执行生产升级。

已是目标结构时，启动账号只需读取 public catalog 和迁移历史；worker 账号需要 Job/Item/ProcessLog 写权限。数据库或租约异常时，先关闭所有重分析 worker，检查 `owner_id`、`fencing_version`、`lease_until`、`attempt` 及关联日志，不手工改 owner/version 或清空队列。参见[故障排查](./Troubleshooting.md)。

---

## 流程三：OSS 准备

| 项目 | 内容 |
|------|------|
| **节点名称** | OSS 准备 |
| **前置条件** | 配置准备完成 |
| **输入条件** | S3 兼容存储服务已部署（如 MinIO/SeaweedFS），`Oss:BucketName` 指定的桶已创建或可自动创建 |
| **操作步骤** | 1. 确认 MinIO/SeaweedFS 已运行<br>2. Local 模式：提供 `Mistake:Local:Oss:*` 六项配置并确认目标桶存在（见 [Local OSS 配置](../Integration/Oss/LocalConfiguration.md)）；Platform 模式：确认 Consul `Oss:*` 与目标桶<br>3. 验证凭证有效性 |
| **成功判定** | 服务启动后，涉及 OSS 的操作（如 `CompleteUploadReview`）可正常执行 |
| **失败后排查方向** | 1. S3 连接失败 → 检查 `Oss:InternalEndpoint` 是否可达、`InternalSecure` 与协议是否匹配、AccessKey/SecretKey 是否正确<br>2. Bucket 不存在 → 手动创建或确认自动创建策略<br>3. Local 模式启动失败 → 检查六项 `Mistake:Local:Oss:*` 键是否全部显式提供 |

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
| **成功判定** | 日志中出现监听端口信息，无异常；出现 `Mistake database schema verified at target version 10` |
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
| **操作步骤** | 1. 存活检查：向 HTTP 端口发送 `GET /health/live`<br>2. 就绪检查：发送 `GET /health/ready` 并核对逐项组件状态<br>3. （兼容保留）`GET /` 仍返回运行字符串 |
| **成功判定** | live 恒 200（纯进程存活，不做任何外部 IO）；ready 全部适用组件通过时 200，任一失败时 503 且响应体逐项列出组件名与失败原因 |
| **失败后排查方向** | 1. live 无响应 → 进程未启动或端口未监听<br>2. ready 503 → 按响应体 `checks` 定位失败组件（见下方处置建议）<br>3. 响应非预期 → 确认请求的是 HTTP 端口 5007 |

### 健康检查方式

```bash
curl http://<host>:5007/health/live    # 存活：200 {"status":"ok"}
curl http://<host>:5007/health/ready   # 就绪：200 或 503 + checks 明细
curl http://<host>:5007/               # 兼容保留："Mistake WebAPI host is running."
```

### 就绪依赖矩阵（IKJFIX）

`/health/ready` 按进程启动时固定的能力快照逐项探测必要依赖，每项独立 3 秒短超时；未启用的能力不出现：

| 组件名 | 何时探测 | 探测方式 |
|---|---|---|
| `db` | 总是 | EF Core 轻量连通探测（等价 SELECT 1 / 打开连接） |
| `oss` | 总是 | S3 桶存在性 HEAD（轻量授权探测） |
| `student` | 仅 `Mistake:Student:Enabled=true` | 对已解析 BaseUrl 的短超时 HTTP 探测，任意 HTTP 响应视为可达 |
| `vl` | 仅 `VlAnalyzer` 已配置（ApiKey 非空） | 对已配置 BaseUrl 的短超时 HTTP 探测，任意 HTTP 响应视为可达 |
| `issuer` | 仅 strict API 认证启用 | 正常 CA/SAN 的 HTTPS discovery 与同 origin JWKS；精确 issuer、非空 keys，拒绝 redirect；共同 3 秒上限 |

规则与边界：

- 两个端点位于 `/api` 组之外，在匿名兼容窗口与 strict 认证模式下都保持匿名（不返回 401）；不接收请求参数。
- 响应只含组件名、ok 与固定短原因（`timeout` / `connect failed` / `probe failed`），不含密钥、连接串、签名 URL；探测日志同样只记录组件名与固定原因。
- 数据库是启动必需依赖：db 地址错误时进程在启动期显式失败（既有 fail-fast 语义不变），`ready` 的 db 项覆盖的是启动后数据库失联的场景。
- `ready` 200 只证明轻量连通性，不代表所有业务事务可成功；live 不得代替 ready 作为流量摘除依据。

运维建议：

- 探测频率不宜激进：ready 每次请求都会产生一次 db 连接与一次 S3 HEAD，编排系统建议 ≥ 10s 间隔并按连续失败次数判定，不要以单次 503 立即重启。
- ready 503 处置：`db` 失败 → 检查 PostgreSQL 可达性与连接串；`oss` 失败 → 检查 S3 endpoint/凭据/桶存在；`student` 失败 → 检查 `Mistake:Student:Enabled` 对应的 BaseUrl 可达性；`vl` 失败 → 检查 VL Provider BaseUrl；`issuer` 失败 → 检查已配置 Authority/Issuer、正常 CA 与真实 discovery/JWKS。任一项持续失败先摘流量再排查，进程无需重启（开关与地址为启动快照，变更需重启）。
- 组件集合随启动快照固定：修改能力开关或地址后必须重启进程，探针矩阵才会随之变化。

> **注意**：健康检查走 HTTP 端口（默认 5007），承载全部 19 个业务端点。mistake→student 的调用也通过 HTTP（见 [ADR-001](../overview/ADR-001-mistake-webapi-only.md)）。

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
| 1 | 服务存活 | `GET /health/live` 健康检查；`GET /health/ready` 就绪探测 | live 恒 200；ready 全组件通过 200、任一失败 503 且 body 标明组件 | 检查进程状态、端口监听；ready 失败按 body 组件名排查对应依赖 |
| 2 | 数据库连接 | 查询错题列表接口 | 正常返回数据 | 检查连接串、数据库状态 |
| 3 | HTTP 端口可达 | portal 调用 mistake 接口 | 正常响应 | 检查 HTTP 端口 5007、防火墙规则 |
| 4 | OSS 读写正常 | 审核完成后检查图片迁移 | `mistakes/` 前缀下存在图片 | 检查 OSS 凭证、桶权限、磁盘空间 |
| 5 | 数据库迁移完整 | 检查 `__EFMigrationsHistory` 和实际 schema | 历史与表结构一致 | 保留备份，核验迁移日志与 schema，不手动 stamp |
| 6 | 旧表转换 | 检查 `public.mistake`、现条目与 `legacy_archive` | 受支持旧表转换后不存在，数据与关系在业务表或归档中保留 | 陌生形态拒绝；保留原库及诊断，按迁移历史核对，不手工删表绕过 |
| 7 | 列名已更新 | 检查 `process_log` 和 `mistake_practice` 表列名 | 使用 `mistake_item_id` 而非 `mistake_id` | 已知旧结构在启动时自动收敛；启动被拒绝时保留数据库与诊断，按[故障排查](./Troubleshooting.md)处理，不手工改列 |
| 8 | 外键约束存在 | 检查 `process_log` 和 `mistake_practice` 的外键 | 存在指向 `mistake_item(id)` 的外键 | 同上；孤儿练习/日志行在收敛时移入 `legacy_archive` 后补外键，不手工补外键 |
| 9 | 索引已创建 | 检查 `mistake_item` 表索引 | 存在 5 个业务索引 | 同上，不手工补索引 |
| 10 | HTTP 请求大小 | 上传含大量图片的错题 | 不超过服务器请求体大小限制 | 如需更大限制，修改 Kestrel `MaxRequestBodySize` |

## Student 本地服务签发 secret

新 `Mistake:Student:ServiceAuthentication:` 配置默认 Disabled（`Enabled=false`），`Enabled=true` 时 Student 能力必须启用。`KeyId` 必须唯一登记于 Student ring，`PrivateKeyPath` 指向 RSA≥2048 PEM 私钥（Linux 拒绝任何 group/other 权限）。`Issuer`、`Audience`、`Subject` 默认值与 [Student 合同](../../../ruoyu.student/docs/Integration/Mistake/ServiceIdentity.md) 一致，显式空值非法。开启时基地址默认必须 HTTPS；只在明确受控测试链路设置 `AllowInsecureHttp=true`，不根据环境名自动降低保护。

`start.sh` 可选读取 `MISTAKE_STUDENT_SERVICE_AUTH_ENV_FILE`（0600）与 `MISTAKE_STUDENT_SERVICE_AUTH_PRIVATE_KEY_FILE`（绝对文件，0600），在停止原容器前检查，再将私钥只读挂到 `/run/ruoyu/mistake-signing-key.pem`。部署目录 0700；env 不保存 PEM 或 JWT。示例 env：

```dotenv
Mistake__Student__Enabled=true
Mistake__Student__ServiceAuthentication__Enabled=true
Mistake__Student__ServiceAuthentication__KeyId=mistake-v1
Mistake__Student__ServiceAuthentication__PrivateKeyPath=/run/ruoyu/mistake-signing-key.pem
```

Student 基地址仍按当前 Platform/Local 模式配置，生产启用应使用受信任的 HTTPS 地址。私 CA 可用 `MISTAKE_STUDENT_SERVICE_AUTH_CA_BUNDLE_FILE` 指定系统 roots 加部署 CA 的绝对 bundle 文件，脚本只读挂到 `/run/ruoyu/student-ca-bundle.pem` 并设置进程 `SSL_CERT_FILE`，保持默认 .NET 证书链/SAN/有效期校验；不得使用无验证 handler 或 `curl -k`。新 Host 文件已经由最小源码 manifest 的递归 Host 成员和 Dockerfile 包含，secret 不 COPY 入镜像；[预演](./MinimalSourceRehearsal.md) 应指定当前提交并显式允许基线漂移。

启用、轮换、65 秒等待与回退顺序由 [Student 部署](../../../ruoyu.student/docs/development/Deployment.md) 承载。隔离双进程 TLS/PG 演练见 [部署验证](../../../../../tests/deployment/README.md)。这里只交付机制与挂载，真实 55.10 的版本、开关和入站资源保护仍由父项协调。

## 隔离严格 Local 运行门禁

[隔离部署验证](./IsolatedDeployVerification.md) 的 `--strict-fixture` 阶段使用真实Production OIDC与任务自有PG/S3验证 Student-disabled Local。strict与resource同时打开、默认 .NET CA/SAN/JWKS校验；单独错Issuer保留真实Authority与token仍401，issuer信任列表遵循[共享认证契约](../../../../ruoyu.common/docs/authentication.md)；普通主体的学生范围访问明确503，admin的合法本域confirm可用，而跨域退回重复503且零副作用。该阶段不启用共享服务或外部Admin，不等于全平台启用批准。维护回退先停自身listener，再以仍为strict/resource=true的已验证配置/二进制重启，或持续关闭listener；不得以恢复匿名200为严格运行的安全回退。fixture必须0600、repo外，公开证据不含secret或签名URL。

## 来源入库启用与回退

`Mistake:SourceIntake:Enabled` 是默认 false 的启动快照。非法 bool 失败；true 要求 Student enabled、strict API + resource auth、受信 issuer/audience/sub/role、有效 RS256 Student private-key secret 和 HTTPS Student URL，任何不完整组合在 DB/S3 初始化前失败。新模式不接受缺少 revision/request UUID 的旧手动调用，也不使用旧 mover。已有受管身份、auth/query/重放和 DB guards 在 false/restart 后永久保留。

启用限于自有隔离验证。共享 capture 前必须停止全部旧 Mistake direct movers、manual CompleteReview 和外部破坏性 StorageAudit，并核对固定消费者已支持新参数；本服务无法阻止另持 S3 凭据的 actor。具备SourceReturns和SourceHandoffs完整恢复协议后仍须核对共享固定消费者/完整引用才能取代旧流程，不能把 SourceRetained 视为 handoff completed 或 GC 授权。

先停掉旧实例，以新版本启动完成自动升级并确认目标结构后再启用；建议升级前保留可恢复备份。回退先停新领取/所有自身 worker，保持新 schema/new binary/guards 和 provider pins；非空禁止 EF Down/旧 binary 降级。如需灾难恢复，停写、真实 pg_dump→隔离 restore、核对完整来源/group/request/IDs/practice/log 与保护并重放同 key 后按正式恢复流程切换。未知 COMMIT 不删图补偿，只 fresh GET/同 key 确认。无超时释放 pin、无 schema 清空回退。

新模式的创建和图片签名读取都依赖可验证原字节；本文上方旧模式“创建不依赖OSS”的表仅描述 legacy 未受管路径，不能用作新入库可用性保证。专项 PG/TLS/文件字节验证命令及专用输入见[能力验证](../modules/SourceIntake.md#验证入口)。

## 图片目标启用与恢复

`Mistake:SourceTargets:Enabled`（`Mistake__SourceTargets__Enabled`）默认 false；true 要求 strict API + resource auth 与现有身份信任配置有效，不要求 source intake/Student capture 同时开启。一个 DB 驱动 worker 启动后及每 20 秒恢复已提交组的缺失目标，Admin POST 可触发相同幂等过程；请求与状态/读取协议见[SourceTargets](../modules/SourceTargets.md)。关闭开关只停止新 copy，保持已有安全读取、状态与 DB guards。

新版本启动时自动升级到目标结构，建议事先保留可恢复备份。空库、六历史已有 item/practice/source/group/replay 库建议先在隔离实例验证；不升级未知 schema。备份必须含 source_target_object/source_target_attempt、来源与组/实际 item/replay及函数/trigger/FK；restore后只读核验 catalog/IDs，再 resume，不能对响应丢失做delete补偿。非空 Down/旧 binary降级拒绝。

每attempt只写独立随机键，非取消Copy可能继续到请求结束后；关闭/停机仍保留reservation和所有候选。安全回退是停止自身新处理、保留schema/guards/来源pins和已有授权读取，或持续关闭listener。部署者须阻止外部旧writer/GC触碰reserved namespace；完整refs/source release不由目标能力证明；forward/reverse saga分别见SourceHandoffs/SourceReturns。

55.10验收仅使用任务自有PG/S3和独立listener/容器，串行执行以控制资源；现行栈、真实配置/credentials和业务数据库保持原样。专项测试可复现输入见能力验证，实际结果写PR/Issue，不写成生产上线证明。

## 受管持久退回启用与恢复

`Mistake:SourceReturns:Enabled`（环境键 `Mistake__SourceReturns__Enabled`）默认 false，非法 bool 失败。true 在 DB/S3 初始化前要求 Student Enabled、strict API/resource auth、Student正常HTTPS 与有效RS256服务签发；无需同时开启 intake/targets 新领取。先发布 Student 的[受保护历史receipt](../../../ruoyu.student/docs/Integration/Mistake/ReturnedReceipt.md)。唯一行为与信任模型见[SourceReturns](../modules/SourceReturns.md)，不以开关启用替代共享 rollout/完整fullrefs。

建议先停所有写方，真实 pg_dump 完整库并在任务自有隔离 PG 恢复检查迁移历史、完整 catalog、原 IDs/practice/log/group/request/target 与所有 return phases/candidates，再以新版本启动完成自动升级。启动只升级受支持结构、不修复漂移；旧misstamp支持路径见数据库迁移文档，未知 public.mistake 不猜测迁移。

false 停本实例新增和恢复推进；其它实例/已发非取消 IO 可以晚到，状态与pins永久保留。重新true启动由DB worker启动及20秒扫描同操作恢复；不能清表、释放snapshot、更换payload、删除候选或匿名降级。Completed认证重放/历史查询与所有guards即使false仍有效。

非空 return 禁止EF Down和旧binary回退；停用保留新schema/binary/guards。灾难恢复必须停写、完整dump→隔离restore→只读catalog核验→恢复同operation，不只恢复item。原提供方合法编辑/删除当前Returned Record后，匹配原历史receipt只完成Mistake，不重建。

实际验证需要任务自有 PG15 与Seaweed4.48随机bucket、正常CA/SAN HTTPS Student及RS256，沿 `MISTAKE_INTAKE_TEST_CONNECTION`/`STUDENT_SCAN_TEST_CONNECTION`、两个PG_CONTAINER backup变量及 `MISTAKE_TARGET_TEST_S3_ENDPOINT`；Seaweed待8333ready再测试，使用volume.max32避免默认卷分配失败。详见SourceReturns验证入口。关闭破坏性外部StorageAudit/GC和旧direct movers，直至完整fullrefs与固定消费者交付。

## 来源交接启用与恢复

Mistake__SourceHandoffs__Enabled=false默认关闭且严格bool；true在DB/S3前要求Student enabled、strict/resource、有效RS256 signer和正常HTTPS。无需intake/targets/returns三开关同时true，已保存真实事实可独立交接。必须先发布Student的SourceHandoffsEnabled/providerM16与永久归档/保护，再以新版本启动完成自动升级（建议事先保留完整备份）；启动只升级受支持结构，不修复漂移。

sealed Manual仅Admin首次固定，worker只恢复已有Manual并扫描完整Automatic；启动及每20秒用现source60/20 lease/fence。关闭停止新处理，完成重放/授权历史/最后guards与全部pins保留。Prepared/Sending响应未知只GET原receipt，Conflict不自动解封，不删图补偿；Student已消费且当前变化/删除时只完成原本域op。

真实pg_dump→自有PG15恢复须包含所有source/group/request/item/practice/log/jobs/targets/returns/候选及source_handoff阶段，Student完整snapshot/receipt/current/fingerprint/tombstone与所有guards、对象桶同时保留。核对原ID、payload、receipt、阶段及lease后恢复同op；非空handoff禁止EF Down/旧binary，回退仅停自身处理并保留新schema/binary/guards或经过完整核验恢复。不能清表、换payload、假确认或匿名降级。

专项测试只用任务自有随机PG库/Seaweed4.48桶，两服务target核验均实际同一S3，正常CA/SAN HTTPS与RS256 uploads.handoff。变量沿SourceReturns，backup容器须与连接同自有PG；通过55.10 SDK8串行运行控制内存。协议/验证策略见[SourceHandoffs](../modules/SourceHandoffs.md)。SourceRetained和HandoffCompleted同时为真不授权物理GC/SourceReleased；外部StorageAudit/旧movers与共享rollout仍由原父项协调。

## 机器 HTTP 合同发布

Host 发布资源携带固定 `mistake-http-v1`，匿名 `/contracts/mistake-http-v1.json` 与业务授权独立；正常 TLS、bytes/hash/ETag、版本兼容与验证命令见[机器合同](../Integration/MachineHttpContract.md)。Docker 发布必须包含该 artifact，不依赖运行目录下的 checkout；无新业务开关、密钥或数据库迁移。

## Student-enabled 两模式受控运行

[隔离部署验证](./IsolatedDeployVerification.md#显式-student-enabled-两模式运行)的 `--student-enabled-fixture` 使用同一指定 commit 的真实 Student/Mistake Release Host，开启 strict/resource、服务 RS256、intake/targets/returns/handoffs，在自有双 PG 与同一 Seaweed bucket 验证业务与恢复。每模式使用独立随机库，Platform 显式拒绝 Consul 端口并使用完整 fallback。全 cell 与原 19 格均通过才成功；任何故障、缺输入或取消保留自身现场并失败，不把 target ready 当 source completed。共享发布仍必须按既有 rollout 门禁。

该运行配置显式使用 `DOTNET_SHUTDOWNTIMEOUTSECONDS=10`，为在途 HTTP drain 和取消设置 .NET Host 预算，使完整进程退出可在30秒期限内验证；不能仅用框架默认30秒 drain 推定总退出时长。配合服务自身的取消/候选保全机制，取消未正常确认的 copy 不发布历史结果，也不补偿删除候选。
