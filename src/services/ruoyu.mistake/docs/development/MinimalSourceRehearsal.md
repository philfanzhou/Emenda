# Mistake 最小源码包 manifest 与仓库外预演

Mistake 服务的最小可构建/可测试/可镜像化闭包由 manifest 固定，配套脚本在仓库外空目录复现完整构建链路，作为未来迁出的自动预演入口。本入口只覆盖构建/测试/镜像面；运行面（隔离环境启动矩阵与平台模式回归）由 [IsolatedDeployVerification.md](./IsolatedDeployVerification.md) 的 07 脚本承接。

## 组成

| 产物 | 位置 | 用途 |
|------|------|------|
| manifest | `src/services/ruoyu.mistake/manifest/mistake-minimal-source-package.json` | 固定源 commit、闭包成员路径（7 个 Mistake 项目 + 3 个共享项目 + sln + .dockerignore）、NuGet 直接依赖 id/版本/许可证表达式、导出排除规则 |
| 预演脚本 | `script/build-script/06-mistake.minimal-source-rehearsal.sh` | 从指定 commit 按 manifest 导出到仓库外空目录，依次执行 restore、Release build、Domain 测试、Service 测试、docker 镜像构建 |

最小闭包成员：Database、Domain、Service、Host、StudentClient、Domain.Tests、Service.Tests（Mistake 7 项）+ ruoyu.common/backend 下 Common、Ai.Shared、Consul.Shared（共享 3 项）+ sln + 仓库根 `.dockerignore` + 权威 `docs/Integration/mistake-http-v1.json` 发布资源。不含 4 个 Portal BFF 共享库、common frontend/docs/test、其他服务与 Portal 源码。

Service.Tests 只保留 portable 核心/宿主测试，不引用 Student 项目或源码。需要真实 Student provider 的来源入库、目标、退回与交接测试由 `tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests` 主责；该项目不进入最小包。仓库常规回归必须执行两套，不能以最小包成功替代 provider 正常 TLS/PG/S3 验证。

## 命令

```bash
# 基线预演（默认使用 manifest 固定的 source_commit）
bash script/build-script/06-mistake.minimal-source-rehearsal.sh --target /tmp/mistake-rehearsal

# 指定 commit（与 manifest source_commit 不同时必须显式允许漂移，实际 commit 会记录在输出与 .rehearsal/actual-commit.txt）
bash script/build-script/06-mistake.minimal-source-rehearsal.sh --target /tmp/mistake-rehearsal \
  --commit <sha> --allow-commit-drift

# 与仓内基线镜像做产物文件名集合对照
bash script/build-script/06-mistake.minimal-source-rehearsal.sh --target /tmp/mistake-rehearsal \
  --baseline-image ruoyu.study.mistake:20260501

# 只重跑已有预演目录的镜像构建阶段（用于残留注入验证）
bash script/build-script/06-mistake.minimal-source-rehearsal.sh --docker-only --target /tmp/mistake-rehearsal
```

## 前提

- 源仓库 git 对象完整（脚本用 `git archive` 导出，只导出被跟踪文件，宿主 bin/obj 天然不会进入导出树）。
- dotnet 8 SDK 与 docker 可用。
- 可选：设置 `MISTAKE_MIGRATION_TEST_CONNECTION` 为专用 PostgreSQL admin 连接串后 Service 测试全量运行；该变量只传入测试进程，脚本不打印、不落盘。未设置时 PG 依赖用例按 `$XunitDynamicSkip$` 门控跳过（表现为失败计数与门控计数相等的"门控跳过"），非 PG 用例必须全部通过。

## 预期输出

脚本按 `[1/9]` 到 `[9/9]` 输出每个阶段的 `PASS:`/`FAIL:`：

1. commit 解析（与 manifest 不一致且未加 `--allow-commit-drift` 即失败）；
2. 闭包成员在目标 commit 全部存在；
3. `git archive` 导出到仓库外空目录；
4. 导出文件集与 manifest 闭包逐项一致（多一个文件或漏一个文件即失败，diff 写入 `.rehearsal/fileset.diff`）；
5. 导出树无 bin/obj 残留、无非闭包内容（Portal/Student/Homework、Portal BFF 共享库、common frontend/docs/test）；
6. `dotnet restore` + `dotnet build -c Release --no-restore` 0 错误；
7. Domain.Tests 全通过；
8. Service.Tests 无 PG 时 PG 用例门控跳过、其余全通过（有 PG 时全量通过）；
9. 镜像构建前二次校验导出树无 bin/obj 残留（脚本先清理自身构建/测试产生的 bin/obj 并留档 `.rehearsal/binobj-byproducts.txt`，残留即显式失败），`.dockerignore` 的 `**/bin`、`**/obj` 兜底模式在位后 `docker build`，产物文件名集合可选与基线镜像对照。

最终输出 `REHEARSAL RESULT: SUCCESS`（或 `FAILED`）。任一步失败保留目标目录现场（`.rehearsal/` 下有各步日志），脚本不删除目标目录、不使用目标外临时目录。

## bin/obj 残留失败模式（已实证）

宿主构建产物 bin/obj 若进入 docker build 上下文，Dockerfile 第 20-21 行源码 COPY 会用宿主 `obj/project.assets.json`（内含宿主绝对路径）覆盖容器 restore 层产物，`publish --no-restore` 失败。脚本双重防护：导出后（步骤 5）与镜像构建前（步骤 9 / `--docker-only`）都校验导出树无 bin/obj 残留。验证方法：向已完整预演（bin/obj 已被脚本清理）的目录注入导出树中原本不存在的 bin 目录，如 `mkdir -p /tmp/mistake-rehearsal/bin` 后执行 `--docker-only`，脚本显式失败（exit 1）并列出残留路径；随后删除注入目录可恢复 `--docker-only` 通过。注意：注入到 `.rehearsal/binobj-byproducts.txt` 已记录的副产物路径（且该路径仍存在）时，该 bin/obj 会随副产物一并清理——docker 上下文同样保持干净，失败模式同样被阻断，但不触发显式失败。

## manifest 演进与重复执行（增量迁移/回退说明)

- **闭包成员变化**（新增/移除项目、改 Dockerfile 依赖目录）：同一提交内更新 manifest 的 `members` 与 `source_commit`（指向新的审计基线），重跑基线预演确认文件集判定仍通过。
- **依赖版本变化**（如安全升级 Steeltoe、ME 包）：同一提交内更新 `dependencies` 对应条目（采集方式：csproj `PackageReference` + `~/.nuget/packages/<id>/<version>/<id>.nuspec` 的 license 元数据），重跑预演。
- **回退**：manifest 是纯数据文件，`git revert` 其所在提交即回到旧闭包定义；预演脚本按 manifest 内容判定，不依赖 git 历史。
- **重复执行**：脚本要求目标目录为空（或不存在）；重复预演换新目录或清空旧目录。`--docker-only` 可在已有预演目录上只重跑镜像阶段。
- 本入口是独立预演命令，不并入 `pre-commit.sh` 快速路径，不改变既有构建命令。

版本化 HTTP artifact 是 Service 的嵌入资源，也由 Host Dockerfile 显式复制；因此 manifest 的 context_files 必须包含它。共享消费者仅 source-link 无领域依赖的 schema 执行器，不属于提供方发布闭包。

跨域 provider 项目通过 test-only friend 与 ProjectReference 复用 Service.Tests 的内部 RunHostAsync；其独立 BaseOutputPath 位于 Mistake Tests 同层的专属 bin 目录，保持既有 fixture 查找真实 Host 的布局。源码与项目仍在 integration 下，输出互不覆盖，不复制 helper，不改变生产可见性。构建产物仍受 **/bin 与 **/obj 排除；最小导出只含 portable Service.Tests。

仓库回归在仓库根运行：

```bash
dotnet build tests/integration/Ruoyu.Study.IntegrationTests.sln --configuration Release
dotnet test src/services/ruoyu.mistake/src/Ruoyu.Study.Mistake.sln --configuration Release
dotnet test tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests/Ruoyu.Study.Mistake.Provider.Tests.csproj --configuration Release
./tests/integration/scripts/pre-commit.sh --skip-docker --skip-e2e --skip-integration
```

pre-commit 的原常规 UNIT 阶段同时执行 core 与 provider；--skip-integration 仅省略可选 Portal integration 集，不省略原 Mistake provider 覆盖。环境沿 SourceIntake/Targets/Returns/Handoffs 的专用 PG15/S3与正常 TLS要求；没有环境时原动态门控如实记录，不能称全量通过。调整源码归属时保持原测试并集/断言不变，manifest 禁止 Student 内容的保证不变。
