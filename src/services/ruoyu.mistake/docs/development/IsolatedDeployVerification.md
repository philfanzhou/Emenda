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

- dotnet 8 SDK、docker（守护进程可达）、python3、curl、git。
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
6. Local 空库 `--upgrade-empty-db` 成功且连续两次执行幂等（自定义库名）。
7. Local 正常启动：`Mistake__Http__Port` 为非 5007 的随机自定义端口；启动日志含 `Mode=Local` 与最终端口与只读 schema 核验行；`GET /health/live` 200；`GET /health/ready` 200 且 checks 恰为 `db`/`oss` 且全部 ok；`GET /` 200 兼容串；匿名兼容窗口 `GET /api/mistakes` 200。
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
- 真实公网代理全链路：公开 URL 仅静态断言 authority/路径前缀重写，不验证公网代理转发。
- 跨服务 E2E 与真实 Student/VL Provider 调用：Platform 就绪探测的 `student` 项按「任意 HTTP 响应即可达」的探针语义满足，不代表业务集成。
- MinIO 兼容镜像来源本身（官方镜像已下架，默认社区镜像，见「前提」）。

## 已知缺陷（截至 2026-10-01）

- 第 11 格（降级面：停止 MinIO 后 `ready` 应转 503 并指明 `oss`）在当前主线会失败：Minio 6.0.2 的 `BucketExistsAsync`/`StatObjectAsync` 吞掉传输层异常并返回成功（上游 [minio-dotnet #1118](https://github.com/minio/minio-dotnet/issues/1118)），`S3OssService.CheckConnectivityAsync` 因此在端点拒连时返回 true，`oss` 组件恒报 ok。缺陷登记与修复方向见 [IKJFJI](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJFJI)；修复合入前本入口在该格如实报告 FAIL（其余格不受影响），修复后同一入口重跑即为全绿判据。

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

每个库经历strict停启与安全回退后仍拒绝匿名，缺CA/错issuer/错ADMINaud新consumer返回401且不匿名降级，缺Authority以键名启动失败。运行中取消先停任务child listener，Popen只管理自身PID；失败保留自己PG/S3和证据，成功按精确owner前缀清理。维护/回退保持strict/resource=true或暂停listener，不能把匿名窗口作为strict安全回退。公开strict-summary只保存status、hash、计数和能力，不保存JWT/code/cookie/密码/连接串/签名URL。

这只证明 Student-disabled Local 的实际身份和能力边界。全平台18端点完整角色矩阵由现有自动测试覆盖，Student-enabled采集/退回/Saga、Portal/Admin真实会话、共享issuer切换及父级最终联验仍按原任务实施；本入口不替代它们。
