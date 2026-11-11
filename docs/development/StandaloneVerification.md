# 独立服务隔离验收

`script/verification/standalone.py` 是新调用方的可重复验收入口。它不启动旧平台、不连接生产数据库、不运行构建或测试项目，也不改全局 Docker context。仅使用 Python 3.10+ 标准库、Docker CLI；本机没有运行中的 Docker daemon 时，可以自动创建任务独占的 Colima profile。

## 边界与真实依赖

- PostgreSQL **15**：全新容器及空库；实际 Host 以 `Standalone/Own` 启动并应用迁移。
- 实际 S3：`chrislusf/seaweedfs:3.85`，启用独立随机凭据，创建私有 bucket，真实 AWS SigV4 PUT、上传及签名 GET；不是内存对象存储或 HTTP 假 S3。验收配置固定 `volume.max=8`、`master.volumeSizeLimitMB=64`，避免默认 30GB 卷规格耗尽小型测试 VM；仍须为镜像、数据库及存储元数据留出空间。
- 实际 Host：将已构建的 Host 输出复制进 `.NET 10 aspnet` 容器，运行 `dotnet /app/Emenda.Host.dll`。不依赖源码挂载，也不隐式运行 `dotnet build/run/publish`。
- VL：独占 Python 容器提供确定性的 OpenAI-compatible 协议 fixture，每张实际图片产生一个独立分组。用于验收入库、恢复及整批成功语义，**不证明真实模型识别质量或外部模型供应商可用性**。
- 每次 `prepare` 使用随机 owner 前缀、端口、API key、数据库及 S3 密钥。PG、S3、Host 对宿主机只发布 `127.0.0.1` 端口；VL 不发布宿主机端口。容器使用独占 bridge network，Host 的数据库、S3、VL 都显式指向该 owner 的依赖，关闭旧平台和外部可选能力。没有使用 Docker `--internal`，因为该 daemon 会禁止 internal network 的端口发布；不把配置隔离夸大为网络层出站防火墙。
- 调用方是可信后端，不是租户；第二个调用方仅验证 `sourceKey` 的 caller 作用域，不声称提供多租户隔离。
- Shared、旧平台、Student、OIDC、生产部署不在验收范围。

MinIO 的部分旧镜像现已无法匿名拉取；此入口固定使用 SeaweedFS，不需要 Docker Hub/Quay 的 MinIO 私有仓库凭据。

## 准备依赖（不会启动 Host 或运行验收）

从仓库根目录执行：

```bash
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/emenda-verification.XXXXXXXX")"
chmod 700 "$WORK_DIR"
FIXTURE="$WORK_DIR/fixture.json"
python3 script/verification/standalone.py prepare --fixture "$FIXTURE"
```

未指定 daemon 时，脚本创建新的随机 `emenda-verify-*` profile，使用 `--activate=false --ssh-config=false --mount none`，仅通过该 profile 的 `DOCKER_HOST` 操作。**不启动、停止或修改 Colima default profile，不执行 `docker context use`。** 自动创建的 profile 会在 `cleanup` 中删除。

若任务已经单独创建了运行中的专用 profile：

```bash
python3 script/verification/standalone.py prepare \
  --fixture "$FIXTURE" --colima-profile "$TASK_PROFILE"
```

此方式不负责启动或删除该 profile；不要传 `default`。Linux 或已有专用 daemon 可以显式传 `--docker-host "$TASK_DOCKER_HOST"`，脚本不会读取全局 active context。

首次需要访问 Docker Hub、Microsoft Container Registry；自动新建 Colima 时可能还需下载 VM 镜像。下载失败会返回非零，不会伪造可用依赖。`prepare` 成功只代表镜像、PG15、真实私有 S3 及桶已准备，不代表业务矩阵已通过。

### Fixture 与保密

`--fixture` 必须在仓库外的现存 **0700** 目录内，文件权限为 **0600**。脚本拒绝覆盖已有 fixture。已有的受限路径可直接交给统一验收步骤，不需要重新准备依赖。

Fixture 包含：显式 `docker_host`、owner、资源名、随机端口、`db_connection`（仅本任务全新 PG）、S3 endpoint/bucket/凭据及调用方 key。它是**秘密文件**，不要 `cat` 到终端、提交、复制进报告或发到聊天。需要 PG 集成测试时，由运行器从 fixture 读取 `db_connection` 并仅传给子进程环境变量；不要把连接串放在命令行参数中。

同一私有目录中的 `*-s3.json`、`*-callers.json` 也是受限 fixture。Host 启动前会预置动态调用方文件 `{"version":1,"callers":[]}`，满足动态凭据文件缺失即拒绝服务的 fail-closed 行为；两个随机静态调用方通过环境传入。

## 统一构建后运行

构建由主验收步骤完成。提供可在 Linux `.NET 10` runtime 下运行的框架依赖 Host 目录，至少包含 `Emenda.Host.dll`、`.deps.json`、`.runtimeconfig.json` 及全部运行依赖。推荐使用统一步骤已有的 `dotnet publish` 输出，而不是缺依赖的单独 DLL；脚本不会补构建。

```bash
python3 script/verification/standalone.py run \
  --fixture "$FIXTURE" \
  --host-dir "$HOST_OUTPUT_DIR" \
  --results "$WORK_DIR/results.json"
```

`run` 首先要求 public schema 没有任何表，然后才启动 Host；因此不能复用已运行过业务验收的数据库来冒充空库启动。再次完整运行需新 `prepare` 一个 fixture。Host 重启步骤是在同一轮内显式验证持久化，不是另起空数据库。

当上传、重放或恢复请求返回契约允许的 `409 source_in_progress` 时，运行器以原调用方、原学生、原 key、原图片重试，最长 120 秒；其他冲突不会被吞掉。成功 `sourceId` 只作为 opaque 标识保存和比较，不要求它等于 `sourceKey`。

### 验收矩阵

| 检查 | 可观察判据 |
|---|---|
| 空库 Own 启动 | 启动前 public schema 为空；实际 Host 启动后 `/health/live`、`/health/ready` 均 200，并存在 migration 15 的 `product_intake_request` 表 |
| API key | 无 key 的业务请求 401 |
| 发布合同 | 匿名获取 `/contracts/emenda-intake-v1.json`，包含上传路由 |
| 学生 ID 边界 | 长度 36 的实际上传成功；37 返回 400 且 `success=false` |
| 真实图片与整批结果 | 上传两张标准库生成的有效 PNG；两个分组、两个不同 item，返回路径覆盖两张图片；数据库计数一致 |
| 私有 S3 签名读取 | 通过现有 `/api/mistakes/presigned-url` 获取签名 URL；真实 S3 GET 字节等于原图；同对象去掉签名后 403；URL 不写报告 |
| 同 key 重放 | 相同请求返回相同 opaque sourceId 和 item IDs，数据库无新增 |
| 冲突 | 同 caller/key 更换学生、图片字节、图片顺序或 media type，返回 `409 source_conflict`，数据库无变化 |
| caller 作用域 | 第二调用方使用相同 sourceKey 可独立创建；不复用第一调用方的 item IDs |
| 并发 | 六个同 caller/key 的相同请求，允许仅对 `source_in_progress` 原样重试；最终返回同一组 IDs，数据库只新增两行 |
| 审核与练习 | 沿 `mistake-http-v3` 的 JSON 合同确认审核，检查外层及内层 success；新增练习后 GET 回读答案、结果和 ID |
| Host 重启持久化 | 同一个实际 dotnet Host 容器 restart；ready 后重放普通及并发 key，IDs 不变、无重复，练习仍可读 |
| S3 故障恢复 | 停止本任务 S3 容器，上传不成功、不创建 item；恢复 S3 后用相同 key/内容完成整批，重放不重复 |
| 分析故障恢复 | fixture 返回 503 时上传不成功、不创建 item；恢复确定性分析后相同 key/内容完整成功 |
| 第二分组入库故障 | 仅在本任务新库临时安装触发器，以非事务 sequence 在第二次 item INSERT 抛错；响应不得报 success，item 和 process_log 数量必须与失败前完全相同；移除故障后同 key/内容恢复，最终恰好两行且重放稳定 |

第二分组故障要求整批事务回滚：失败后 item 和 process_log 都不得留下部分提交；恢复后不能缺图或重复。S3 的确定性路径对象允许保留原内容以便原请求恢复，不要求数据库失败时回滚对象存储。触发器、函数和 sequence 在 `finally` 中删除；若进程被强制终止，直接清理整个 owner 的隔离依赖，不对其他库执行修复 DDL。

## 结果与故障诊断

正常输出只包含检查名和报告路径。`results.json`（0600）只包含脱敏的逐项结果、数量、时间、最终 success 和固定诊断，不保存密钥、连接串、业务响应、Host 原始日志或活签名 URL。运行器失败返回非零；矩阵中未执行的检查不会被记为通过。

`run` 成功时删除该轮 Host 容器，失败时保留它供诊断；PG/S3/VL 依赖均保留到显式 `cleanup`，失败也会尝试复位 VL。上传失败诊断只打印 HTTP 状态和有限集合内的 errorCode，不打印响应体。原始 Docker stderr 默认抑制，因为错误可能包含敏感配置。需要调查时只使用 fixture 指向的专用 daemon 和 owner 容器，私下查看，不把原始容器 inspect 或日志直接贴到公开报告。端口在准备时由系统分配；其他进程抢占端口会让准备/启动失败，应清理该 owner 并新建 fixture，而不是切到共享端口或修改全局配置。

本文件描述的是**入口覆盖和运行方式，不是已通过记录**。以对应运行生成的 `results.json`、构建/测试输出为准；依赖准备不等于自动检查、业务验收或部署完成。

本次实际执行结果及二进制身份见 [StandaloneVerificationResult](StandaloneVerificationResult.md)。

## 清理

```bash
python3 script/verification/standalone.py cleanup --fixture "$FIXTURE"
```

仅处理 fixture 记录且 owner label 匹配的容器及网络，删除容器的匿名卷，不执行 `docker system prune`，不扫描/删除其他资源，不停止全局服务。自动创建的同 owner Colima profile 最后删除。若通过 `--colima-profile` 复用专用 profile，其生命周期由创建者负责（容器/网络仍由此入口清理）；不得清理 default profile。0600 fixture 与脱敏报告保留，便于对账，由创建者在不再需要后自行删除。
