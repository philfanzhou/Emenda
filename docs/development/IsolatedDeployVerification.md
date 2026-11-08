# Mistake 隔离部署验证入口

在只有 dotnet SDK 与 docker 的干净环境里，从指定 commit 出发复现 Mistake 服务的独立运行基线：临时 PostgreSQL 15 与 S3 兼容对象存储（MinIO 兼容容器）全部使用随机高位端口、随机自定义库名/桶名/公开 URL，逐格验证 Local 模式启动矩阵、失败诊断面、降级面与 Platform 模式回归，并把可读取证据落盘。本入口是 [IKJ0IL](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IL) 剩余「隔离部署验证」的可重复收敛，不并入 `pre-commit.sh` 快速路径。

## 组成

| 产物 | 位置 | 用途 |
|------|------|------|
| 验证脚本 | `script/build-script/07-mistake.isolated-deploy-verification.sh` | 从指定 commit 导出源码并 `dotnet publish`（Release），启动临时 PG/MinIO 容器，执行 19 步验证矩阵 |
| 证据目录 | `--target` 指定目录下的 `.deploy-verify/` | 逐格日志（`cells/`）、发布日志、脱敏环境记录、实际 commit、汇总 `summary.txt` |

## 命令

```bash
# 默认从当前 HEAD 出发（实际 commit 记录进证据）
bash script/build-script/07-mistake.isolated-deploy-verification.sh --target /tmp/mistake-deploy-verify

# 指定 commit 与镜像（镜像均可覆盖）
bash script/build-script/07-mistake.isolated-deploy-verification.sh --target /tmp/mistake-deploy-verify \
  --commit <sha> --pg-image postgres:15 --minio-image pgsty/minio --mc-image pgsty/mc
```

## 前提

- dotnet 10 SDK、docker（守护进程可达）、python3、curl、git。
- 目标目录必须为空或不存在，且位于源仓库之外。
- PG/MinIO 兼容镜像可拉取。默认 `postgres:15` 与 `pgsty/minio` / `pgsty/mc`：MinIO 已于 2025-09 从 Docker Hub 下架全部官方镜像（其后 Quay 同样受限），`minio/minio` / `minio/mc` 不再可拉取，默认指向承载同一 server/mc 二进制的社区镜像；部署者可用参数固定到自己镜像仓库中的副本。
- 不依赖原平台网络、Consul、Loki、真实 Student 或 VL Provider；本机 5432 被占用不影响（全部端口为 20000–44999 的随机空闲高位端口）。

## 验证矩阵

脚本按 `[n/19]` 输出每步 `PASS:`/`FAIL:`。矩阵格失败不中断运行：后续格继续采集证据，最终结论按 `summary.txt` 汇总判定，全部通过输出 `DEPLOY VERIFY RESULT: SUCCESS`（任一格失败输出 `FAILED` 并列出失败格）；基础设施失败（发布、容器、清理断言）立即中止。匿名兼容窗口的业务 GET 使用规范零值过滤串（`subject=0&grade=0&reviewStatus=0&page=1&size=10`）：minimal API 绑定把显式值类型查询参数按必填处理，空查询串会得到空体 400（与既有 strict/resource 测试的调用形态一致）。

1. commit 解析：`--commit` 或当前 HEAD 解析为实际 commit 并写入证据。
2. `git archive` 导出闭包（Mistake src + 三个共享库），不使用仓库内构建产物。
3. `dotnet publish` Host（Release）——构建路径选择记录在 `.deploy-verify/build-path.txt`。脚本把发布的 `appsettings*.json` 复制进每个 cell 工作目录：以 `dotnet <publish>/Host.dll` 从其他目录启动时 content root 是 CWD，进程旁没有该文件会使 Platform 模式的 Serilog `ReadFrom.Configuration` 解析不到任何 sink 而静默丢弃全部日志。
4. 临时 PostgreSQL（随机高位端口、随机自定义库名/用户/密码，仅绑定 127.0.0.1）。
5. 临时 MinIO 兼容容器（随机高位端口、随机凭据、随机自定义桶）并种入 `mistakes/` 前缀对象。
6. Local 空库 `--upgrade-empty-db`（兼容命令，执行与启动相同的自动升级）成功且连续两次执行幂等（自定义库名）。
7. Local 正常启动：`Mistake__Http__Port` 为非 5007 的随机自定义端口；启动日志含 `Mode=Local` 与最终端口与 `Mistake database schema verified` 核验行；`GET /health/live` 200；`GET /health/ready` 200 且 checks 恰为 `db`/`oss` 且全部 ok；`GET /` 200 兼容串；匿名兼容窗口 `GET /api/mistakes` 200。
8. 预签名 URL：`POST /api/mistakes/presigned-url` 响应 URL 的 scheme/authority 与路径前缀等于自定义 `PublicBaseUrl`（静态断言；真实公网代理链路为排除项）。
9. 能力关闭语义：`ReviewStatus=Rejected` 审核两次请求均返回 503 稳定响应体 `{"success":false,"message":"Student 集成未启用，退回审核不可用（Mistake:Student:Enabled=false）"}`（该分支在任何领域/OSS/Student 副作用之前返回）。
10. 失败诊断面（四格，均以键名报错退出且未进入数据库阶段）：缺 `ConnectionStrings__MistakeLocal`；`Mistake__Http__Port` 越界（70000）；`Mistake__Student__Enabled=true` 缺 `Mistake__Local__Student__Url`；worker 开关非布尔值。
11. 降级面：停止 MinIO 后 `/health/ready` 503 且 checks 指明 `oss=false`（`db` 仍 true），`/health/live` 恒 200。
12. SIGTERM：Local 进程 30 秒内退出，日志无未处理异常栈。
13. 重启 MinIO 并在同批临时 PG 创建第二个自定义库（Platform 回归用）。
14. Platform 模式 `--upgrade-empty-db` 经 `ConnectionStrings:Default` 回退成功。
15. Platform 正常启动：Consul 被显式指向确认关闭的本地端口（保证「Consul 不可达」前提确定成立），启动不被阻断，日志含可读的 Consul 失败/回退诊断；`Mode=Platform`；worker 与 Student 能力按平台缺省 `true` 出现在启动日志。
16. Platform 探测与业务面：`/health/ready` 200（db/oss 均 ok；Student 就绪探测按「任意 HTTP 响应即可达」语义指向临时 MinIO 端点）、`GET /api/mistakes` 200。
17. Platform SIGTERM 优雅退出。
18. 清理：仅删除自身命名前缀 `mistake-deployverify-<hex>` 的容器与网络（逐个核对名称后删除），并断言无残留容器/卷。
19. 证据汇总落 `.deploy-verify/`。

## 安全与清理

- 脚本生成的 PG/MinIO 凭据只进容器与子进程环境变量，不写入证据、不打印明文；证据中的环境记录为脱敏版（`env-redacted.txt`）。
- 容器/网络/（如有）卷全部带随机命名前缀；删除前逐个核对名称，拒绝删除无关容器。成功运行结束后断言无前缀残留；失败运行保留现场（容器名与证据路径会打印），排查后手动 `docker rm -f` 前缀容器即可。
- 连续两次执行均应 SUCCESS（每次使用全新目标目录与全新随机容器）。

## 与其他入口的分工

- [MinimalSourceRehearsal.md](./MinimalSourceRehearsal.md)（06 脚本）覆盖仓库外构建/测试/镜像预演，不含运行矩阵；本入口覆盖「运行面」，可复用其镜像思路但默认走 `dotnet publish`。
- [IntegrationVerification.md](./IntegrationVerification.md) 是业务联调用例清单；[BackupRestoreDrill.md](./BackupRestoreDrill.md) 是数据库备份演练——二者与本入口互不替代。
- 联合验证/观测/文档收口入口由 [IKIZY6](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY6) 持有，以本入口的矩阵证据为输入。

## 已知排除项

- Local 匿名 API 的公网安全性（[IKIZY0](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY0) 切片③外部前置）。
- 原19格的公网代理全链路：公开 URL 仅静态断言 authority/路径前缀重写。下述strict阶段单独验证任务自有公开入口实际签名读图，不替代共享生产代理联验。
- 跨服务 E2E 与真实 Student/VL Provider 调用：Platform 就绪探测的 `student` 项按「任意 HTTP 响应即可达」的探针语义满足，不代表业务集成。
- MinIO 兼容镜像来源本身（官方镜像已下架，默认社区镜像，见「前提」）。

## 显式真实 OIDC 严格 Local 验证

原19格仍覆盖匿名兼容/Platform基线。显式增加 `--strict-fixture /absolute/path/fixture.json` 时，脚本导出同commit的测试helper、复用Release发布与任务自有PG/S3，在清理前执行第20阶段，两次独立随机库均保持 Student/三worker关闭、VL未配置、strict/resource均true。缺输入、非0600文件、symlink、缺issuer/callback/CA/ADMIN/PROBE_OTHER/合成账户时直接失败，不能跳过再输出SUCCESS。

```bash
bash script/build-script/07-mistake.isolated-deploy-verification.sh \
  --target /tmp/mistake-strict-verify --commit <actual-sha> \
  --strict-fixture /absolute/path/fixture.json
python3 tests/deployment/test_strict_local_probe.py
```

fixture由已授权的真实provider部署者写到repo外：键为 `issuer`（HTTPS authority）、`callback`（注册的精确 `/api/auth/oidc/callback` HTTPS URL）、`ca`（公开部署CA路径）、`apps`（ADMIN、PROBE_OTHER两项confidential secret字符串）、`admin` 与 `ordinary`（各自username/password）。文件0600，不在仓库放真实值。task-owned SignaCore Production前置入口为 `https://192.168.55.10:25443`、callback `https://192.168.55.10:25444/api/auth/oidc/callback`；授权caller仅读取其受限fixture secret，不改provider keys/config/data seeds。

caller复用真实登录cookie/CSRF、Code/S256与真实RS256/JWKS验证；接收精确注册redirect在内存完成state/iss/nonce/subject校验与confidential兑换，并正常TLS检查保留callback的公开health入口，不替换远端callback。这个测试caller不是外部Admin UI/BFF。令牌仅内存，普通无admin与其他audience均由同一个真实provider签发。若caller环境需要TCP路由，只能通过fixture的caller transport routes保持原URL/Host/SNI及正常CA/SAN验证；不改全局DNS/trust，不使用-k或不安全handler。Mistake进程用系统roots+部署CA的 `SSL_CERT_FILE`，HTTPS metadata及签名/issuer/audience/lifetime校验保持启用。

矩阵逐项断言匿名/其他真实audience/篡改签名401；真实ADMIN admin读取item/list/by-upload/practice/log200；普通主体staff/admin-only403、Student归属访问503；伪reviewer403；合法reviewer重复退回503；admin写practice/log403；合法confirm持久本域review/log变化。拒绝前后真实PG全部行/关联与S3 inventory摘要相等，签名图片经过任务自有公开代理按原签名Host和剥离前缀实际读取bytes；confirm/重启图片摘要不变。Student/Consul/Loki共用sentinel计数必须0。

两轮使用独立的新库和新桶，每个库经历strict停启与安全回退后仍拒绝匿名，缺CA/单独错Issuer/错Authority+Issuer/错ADMINaud新consumer返回401且不匿名降级，缺Authority以键名启动失败。单独错Issuer负例保持真实Authority、CA、ADMIN受众与token不变，仅改Issuer；默认与Admin scheme均只接受显式Issuer及AdditionalValidIssuers，metadata不扩展该列表。另保留错误Authority+Issuer来源负例，不声称覆盖全平台issuer迁移。运行中取消先停任务child listener，Popen只管理自身PID，credential-bearing子进程禁用自身core dump；失败保留自己PG/S3和证据，成功按精确owner前缀清理。维护/回退保持strict/resource=true或暂停listener，不能把匿名窗口作为strict安全回退。公开strict-summary只保存status、hash、计数和能力，不保存JWT/code/cookie/密码/连接串/签名URL。

这只证明 Student-disabled Local 的实际身份和能力边界。全平台18端点完整角色矩阵由现有自动测试覆盖，Student-enabled采集/退回/Saga、Portal/Admin真实会话、共享issuer切换及父级最终联验仍按原任务实施；本入口不替代它们。

## 显式 Student-enabled 两模式运行

`--student-enabled-fixture /absolute/path/fixture.json --seaweed-image <image@sha256:digest> --transport-image <python-3.11@sha256:digest>` 在原19格及可选 strict 阶段之后，串行启动同 commit 的真实 Student/Mistake Release Host。fixture 沿上述真实 provider 字段，并增加 `runtime_image`（固定 .NET 10 ASP.NET 镜像 digest）和 `issuer_control`（`container`、`label`、`owner`，只能控制部署者授权的 owned provider 容器）。文件0600、父目录0700、不得 symlink；缺输入或所有权不匹配直接失败。Seaweed 使用 `volume.max=32`、800MiB 上限，等待未认证 S3 root 返回403后建随机桶；无需预装 MinIO Server。MC 仍由显式镜像提供建桶/seed工具。

```bash
bash script/build-script/07-mistake.isolated-deploy-verification.sh \
  --target /absolute/owned/runtime-run --commit <exact-sha> \
  --pg-image <postgres-15.18@sha256:digest> --mc-image <mc@sha256:digest> \
  --seaweed-image <seaweed-4.48@sha256:digest> --transport-image <python-3.11@sha256:digest> \
  --strict-fixture /absolute/private/provider.json \
  --student-enabled-fixture /absolute/private/provider.json
python3 -m unittest discover -s tests/deployment -p test_runtime_transport.py -v
```

Local/Platform 是 ConfigMode，两者均真实 S3。每模式新建两个 PG 库，actual Student 位于 owned Docker network、固定5005，前方是自有正常 CA/SAN HTTPS transport；PG fault transport是同自有 network 的80MiB独立容器（Student走内部DNS5433，Mistake走绑定127的随机高端口），不开放共享防火墙；Mistake 是独立原生进程与随机 HTTP1 端口。共享环境的 DNS、CA、issuer密钥/seed、KV与业务库不修改。VL 仅使用明确授权的确定性兼容 HTTP dependency；真实上传、分类、采集与后续 worker 均走生产 Host。当前 SimpleClassifier 全部标 mistake，mixed 格在真实上传/分类后、首次 snapshot 前只通过正常 SQL guard 设置新增 homework 类型，报告这一受控输入边界。

两模式显式提供 .NET Host 的 `DOTNET_SHUTDOWNTIMEOUTSECONDS=10`，为在途 HTTP graceful drain 留出有界取消预算，在30秒运维期限内退出。默认 .NET Host 的30秒 drain 不能直接当成“总退出小于30秒”的保证；完整复现必须保留本项配置。native进程与携带凭据的 Student/PG transport 容器均禁用 core dump。

逐 cell 覆盖实际启动与5项依赖、单/多图/多crop/manual多批、完整 automatic worker、正反 receipt、练习/reanalysis/签名图片、合法 COW/newrevision、永久重放；Student/Mistake COMMIT 前/中/后、S3 Copy 后丢响应、历史 GET 失败、两个独立实例竞争与旧 fence 拒绝（旧 worker 的真实 VL 在途时只停本任务 PG transport，新实例走相同 owned PG 的独立连接，按数据库自然时间接管，迟到分析不能重复提交）；正常业务认证/TLS、逐个停止自己的 PG/S3/Student/provider并恢复、显式关闭、实际1..N已知历史升级（N 由 `RuntimeSchemaFixture count` 从程序集读取）、双库完整 dump→新库 restore、guards与非空 Down、unknown schema、30秒SIGTERM重启及真实 reverse Copy 在途取消。Provider token 过期格等待真实签发的 `exp` 加启动快照30秒 skew，不伪造签发、不改全局时钟，可能需要等待签发方既有 TTL；等待期间只输出剩余秒数。全状态格通过真实依赖故障产生 forward/reverse Prepared、Sending、Conflict、Completed 和全部候选阶段，显式关闭能力后 dump/restore，确认全字段历史一致，再恢复受控依赖与能力、以原 operation/payload 推进可恢复事实。故障在恢复前单独保存事实与真实结果；PG during 格使用仅 owned DB 的临时 deferred trigger 等待，并确认选中 backend 在 PgSleep 后终止，移除临时 trigger后才冷启。未知 S3响应保留已实际复制的随机候选键，不覆写、不补偿删除。

当前 `S3OssService.CopyObjectAsync` 实际采用 GET 源对象、PUT 候选对象；故障 transport 按该格精确 owned source/item 的候选前缀，在最后 PUT 已由真实 S3 返回200后丢响应，并另保留 server-side COPY 协议测试。不能仅等待 `x-amz-copy-source` 来证明当前生产 copy 在途。

`.deploy-verify/enabled/summary.json` 保存固定 head/digest/UTC、每 cell 布尔、0 dependency skips、owner manifest 与 before/after 无关容器核对（Mounts仅按Destination规范无序集合，保留全部字段）。受限 Host 日志、双库 dumps、连接/证书/服务密钥与恢复参数在 target 的 `private/`，不可公开上传；公开报告只保存 hash/count/status，不保存 bearer、code/cookie、密码或签名URL。失败保留属于本次 owner 的容器/库/对象/日志；成功清理时同时核对精确名称及 owner label，删除自身匿名 PG volume，拒绝无关资源。程序只停止其自身 Popen PID，取消传播至自身 listeners，并保留已提交历史；不得清理归档或物理 GC。

本地 transport 测试只验证协议 fault 选择和安全参数退出，不代表真实运行验收；只有完整矩阵结果可作为运行门禁。诊断用 subset 明确不是完整验收并返回失败，不能用于发布 SUCCESS。该运行证据不关闭原父的 fullrefs/fixed Admin/legacy/physical release 范围。

旧 owner 场景仅停止该 Mistake 实例独立的自有 PG transport；Student 与接管实例保持真实 PG 连通；接管后按有界 readiness 恢复旧实例 PG，再等待旧 VL 响应尝试。VL 证据分别记录旧响应在接管后的发送尝试与 socket write 结果，后者不等于客户端已经消费响应；无论旧请求被取消或迟到，都核对同一 snapshot、新 fence、唯一 group 与 receipt，不把尝试计数冒称实际送达。

双实例 HTTP 竞争与自然 lease 接管完成后，旧 fence 的直接 SQL 事务拒绝验证先停止两个自有 Mistake writer，再保存完整重复读快照，要求真实 `P0001/source_managed` 和全部表、行、字段逐字不变；随后重启 Host 完成原目标、业务确认与唯一 receipt。冻结仅用于区分拒绝事务的副作用和合法 target worker 推进，不减少独立旧 worker 在途竞态，也不排除任何历史或操作字段。

依赖恢复最多等待180秒，ready探测间隔5秒并保存各组件真实恢复结果；每次探测包含实际 discovery/JWKS 请求，不能以高频重试耗尽 owned provider 的 metadata 限流预算。保持 provider 正常限流、TLS 与身份配置，不用关闭校验来恢复通过。

Student 活跃重启的比较保存每库每表完整行快照，不把当前上传记录的所有操作元数据当作冻结历史。只有实际 worker 日志证明 `Processing→Pending→Processing` 的三次 Save，且同一 ID、状态2、content revision、所有其他字段/所有其他表严格一致时，允许 `Version` 精确+3、`ProcessingEnteredAt` 与 `UpdatedAt` 落在本次重启窗口内；每个变化逐行登记。字段、行集、历史 receipt 或业务事实的其他差异立即失败。依赖恢复与 SIGTERM 使用此有界因果断言；停止全部写入后的完整 dump/restore 仍要求全部字段、schema 和历史逐字一致。
