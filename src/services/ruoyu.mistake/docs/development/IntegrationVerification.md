# ruoyu.mistake 集成验证入口

本文说明怎样判断一次联调是否完成，以及到哪里运行对应检查。字段、权限、状态和迁移规则分别由下列权威文档定义；这里不维护第二套接口规格。实际测试结果应保存在对应 Issue 或验证输出中，不能把本文当作“已经全部通过”的报告。

## 1. 开始前确认

- 固定本次 Study commit、Student/Mistake 配置、外部 Admin 与身份服务的实际版本，并记录命令、用时、结果和输出路径。跨服务验收使用同一组合，不能拼接不同版本的绿色结果。
- Mistake 与 Student、三个 Portal 之间使用 HTTP。Mistake 的业务入口以 `/api/mistakes` 为前缀；Portal 调用地址为 `MistakeService:Url`。路由、JSON 字段、枚举、认证和错误条件以[版本化机器 HTTP 合同](../Integration/MachineHttpContract.md)及其发布的 JSON 为准。
- 使用任务自有 PostgreSQL 15 数据库、S3 桶、账号与正常 CA/SAN HTTPS。不要在共享业务库注入故障，也不要停止共享身份服务来测试恢复。
- 开关和服务凭据按[部署说明](./Deployment.md)配置。`Local` 是独立配置模式，正常运行仍使用 S3；Mistake Host 没有文件系统存储回退，不能用 `USE_LOCAL_OSS` 绕过 OSS 故障。
- 先停会删除原图的旧 Worker、旧聚合审核迁移和外部破坏性清理，再验证受管来源流程。关闭新处理开关不会解除已有来源、候选和历史的保护。

“受管”指已经保存不可变来源及其题目关联的数据，见[来源入库](../modules/SourceIntake.md)。尚未建立这些关联的旧条目另按第 4 节核对，不能用旧行为解释受管数据。

## 2. 沿业务步骤检查

### 身份、输入和真实消费者

读取发布的 `GET /contracts/mistake-http-v1.json`，核对内容 hash、条件 GET 与运行时路由清单。业务请求既检查 HTTP 状态，也检查合同规定的响应结构：普通响应外层 `success=true` 可能仍包含内层 `data.success=false`；空体、坏 JSON、未知响应和传输故障不能猜成成功。

受管创建、读取、图片访问、审核和重分析的可信主体与归属检查不随开关关闭而失效；审核人的 `reviewerId` 必须与可信主体匹配。intake 管理查询、目标恢复、handoff 和对账始终要求认证的 Admin。Mistake 调用 Student 使用[服务身份](../../../ruoyu.student/docs/Integration/Mistake/ServiceIdentity.md)，不转发用户令牌；快照、退回回执、完整交接各有对应权限。认证失败不匿名重发。

非法学生、来源、学科、年级、图片或区域输入按当前 operation 的 HTTP/业务错误断言。创建要求 subject 1–9、grade 1–12；审核只接收 Confirmed 或 Rejected，有效的可选年级/学科才修正原值。分页、过滤、缺失条目和绑定错误也按合同逐项检查，不以旧 RPC 状态或精确异常文本作标准。

实际共享 HttpClient 与三个 Portal 必须保留未完成和失败：受管 review 的 202 是“处理中”，内层 false；界面不显示“已退回”，不提前关闭。下游业务失败、权限拒绝、未知响应和取消均应按[共享调用边界](../../../../ruoyu.common/docs/bff-shared/MistakeBff.HttpClients.md)处理；失败不能变成成功空列表，前一步失败后不能继续 Student 写入。

### 采集与创建

Mistake 的 `UploadPollingWorker` 主动扫描 Student 的 Processing 记录；Student 不通过 RPC 推送错题。启用[来源入库](../modules/SourceIntake.md)时，Worker 与现有 `POST /api/mistakes/upload`、`POST /api/mistakes` 共用不可变快照和入库结果。手动请求按规格提交 `expectedContentRevision` 与 `requestKey`，不把扫描得到的路径当成可信完整来源。

成功证明是已提交的题目、Created 日志、来源分组关联和请求结果。相同请求重放得到原 ID，不增加题目、不重跑分析；异载荷或冲突分组明确失败。自动多图分组必须整体提交，字节核验或分析失败不能留下部分成功。创建阶段保留原图和快照，不删除 Student 来源、不执行完成交接。

### 图片目标与读取

[目标物化](../modules/SourceTargets.md)先保存复制尝试，再复制实际引用的原图并核验长度和摘要。多区域共享原图不重复产生业务目标；未引用图片不冒充目标。复制响应丢失、取消、进程停止或旧处理者迟到时，恢复沿原持久关联推进，不覆盖选定目标、不删除候选补偿。

`POST /api/mistakes/intakes/{sourceId}/{revision}/targets` 与对应 GET 用来核对目标状态。`targetReady=true` 只证明当前已提交组的图片齐备，后续合法组可使它再次变为 false；它不能证明 `handoffCompleted=true`。

`POST /api/mistakes/presigned-url` 读取受管原图或选定目标时，核对真实 item/student 关联、精确对象键及字节。来源原图与目标只允许 `size=full`；练习图按自身既有大小规则处理。错误大小写、跨学生、未选定候选或篡改字节均失败，不签发可用地址。测试输出不保存活的签名 URL。

### 审核、练习与重分析

审核入口为 `POST /api/mistakes/{id}/review`。确认更新审核状态、元数据和 ReviewConfirmed 日志，不搬图、不移除 Student 来源，也不证明完整来源交接已完成。

[受管退回](../modules/SourceReturns.md)先保存意图和原审核内容，再准备全部原图副本，以同一操作向 Student 发起退回。只有可信 Student 原历史回执匹配，并且 Mistake 同时提交 Completed、Rejected 和唯一最终日志，才是成功。POST 响应或当前某条 Returned 记录不足以证明完成。多图部分复制失败、结果未知或回执查询故障都保持未完成；恢复使用同一操作，不重建已被学生合法编辑、消费或删除的当前记录。

受管退回成功保留 item、练习、日志、来源、创建结果和全部图片引用。默认列表可能排除已完成退回条目，但授权详情和显式 Rejected 查询仍可读取历史；“默认列表为空”不等于数据库已物理删除。已有退回意图阻止同条目的确认、编辑、练习、日志和重分析写入，不能通过旧 SQL 或旧 EF 写方绕过。

练习写入验证 Confirmed 前置、学生绑定、持久练习与 Practiced 日志；处理日志的合法动作取[当前领域枚举](../../src/Domain/Models/MistakeEnums.cs)，不能直接套用旧表动作编号。重分析验证 Pending/Running/完成或失败状态、输入变化、两个实例竞争、租约接管、取消和原子结果，不能以排队成功当作分析完成。详细入口见[审核说明](../modules/MistakeReview/ReviewMistakeItem.md)和[重分析文档](../modules/MistakeReview/ReanalyzeMistakeItem/README.md)。

### 完整来源交接

[来源交接](../modules/SourceHandoffs.md)核对来源的全部图片与全部真实分组：所有条目已确认，或已有完整退回成功证明；目标齐备。手动来源必须由可信 Admin 显式封口，不用第一批图片就封死后续合法分组。

`POST /api/mistakes/intakes/{sourceId}/{revision}/handoff` 保存固定操作并请求 Student 条件消费同一快照。只有可信[原交接回执](../../../ruoyu.student/docs/Integration/Mistake/SourceHandoff.md)匹配且本域 durable Completed，才返回 200、内层 true；202、内层 false 表示尚未完成。状态查询中的 `SourceRetained` 与 `handoffCompleted` 可以同时为 true。

Student 当前记录可能因完整消费而被移除，或保留其他分类图片；其结果必须与原历史回执一致。已取得的快照、原图和 Mistake 全部历史永久保留。因此不要要求“来源原图必须不存在”，也不要以当前 Student 记录是否存在代替历史完成证明。

### 完整引用与只读对账

[StorageReferences v1](../../../../../docs/integrations/StorageReferences.md)从 Student、Mistake、Homework 各自捕获完整持久引用快照并分页返回。验证涵盖所有状态的条目、练习、来源快照、候选、退回和交接引用；对象键精确区分大小写和 Unicode。任一来源不可达、页缺失、解析失败、摘要或完整性不符，都不能返回部分快照或成功空集。

实际 Admin 消费方收齐并验证三方全部页后才能进行对象列举。结果只能表示“未观察到引用”；`deletionAuthorized=false` 恒成立，完整快照也不授权删除或复制。引用审计不是普通条目分页拼接，也没有旧 `GetAllReferencedImagePaths` RPC 可调用。

`GET /api/mistakes/reconciliation` 的[对账报告](../modules/SourceReconciliation.md)只报告缺失/冲突/待恢复/未受管引用及修复预览。读取失败不输出部分报告；报告前后业务数据和对象字节不变。没有可信旧来源时标为未知，不据此补建记录、搬图或删图。

## 3. 故障与恢复检查的位置

以下是现有自动测试入口；测试文件存在不表示本次运行已经通过。提供方测试目录为 [Ruoyu.Study.Mistake.Provider.Tests](../../../../../tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests)，服务测试目录为 [Mistake.Service.Tests](../../src/Tests/Ruoyu.Study.Mistake.Service.Tests)。真实 PG/S3/HTTPS 与替身测试的结果分开记录；必需输入缺失或测试跳过均保留未完成。

| 检查内容 | 对应既有测试与权威说明 |
|---|---|
| HTTP 发布、路由、字段、错误、实际消费者 | `MistakeHttpContractTests`、`MistakeHttpContractWireTests`、`MistakeConsumerHostModesTests`；[合同可执行检查](../Integration/MachineHttpContract.md#可执行检查)及其中的实际共享 HttpClient/三 Portal 测试 |
| 来源同请求重放、异载荷、多图整体创建、两实例竞争、提交响应丢失与取消 | `SourceIntakePostgresTests`、`SourceIntakeSecurityTests`；[来源入库验证](../modules/SourceIntake.md#验证入口) |
| 真实复制、核验失败、部分目标、重启恢复、晚到处理者与关闭开关后的保护 | `SourceTargetPostgresTests`、`SourceTargetS3Tests`；[目标物化](../modules/SourceTargets.md) |
| 多图退回、各提交阶段故障、回执丢失、取消/晚到 Copy、并发写入、恢复与备份 | `SourceReturnPostgresTests`、`SourceReturnRecoveryPostgresTests`、`SourceReturnS3Tests`、`SourceReturnStartupTests`；[退回验证](../modules/SourceReturns.md#验证入口)及其中的真实 BFF/ReviewDetail 检查 |
| 完整封口、混合确认/退回、条件消费、两个实例/新 revision、取消/重启与关闭开关 | `SourceHandoffPostgresTests`、`SourceHandoffRecoveryPostgresTests`、`SourceHandoffS3Tests`、`SourceHandoffStartupTests`；[交接验证](../modules/SourceHandoffs.md#验证策略) |
| 全部引用、坏持久数据/部分页、取消和禁止物理清理 | [公共引用合同测试](../../../../ruoyu.common/test/Ruoyu.Study.Common.Tests/StorageReferenceContractTests.cs)、[Student 引用 PG 测试](../../../ruoyu.student/src/Tests/StorageReferencePostgresTests.cs)；[三方提供者与实际 Admin 的联合验证要求](../../../../../docs/integrations/StorageReferences.md) |
| 对账异常、读取失败、截断计数与零写入 | `SourceReconciliationPostgresTests`；[对账验证](../modules/SourceReconciliation.md#验证策略) |
| 重分析提交未知、输入变化、独立 worker、取消与旧租约失效 | [ReanalyzeLeasePostgresTests](../../src/Tests/Ruoyu.Study.Mistake.Service.Tests/ReanalyzeLeasePostgresTests.cs)、[ReanalyzeJobServiceTests](../../src/Tests/Ruoyu.Study.Mistake.Domain.Tests/ReanalyzeJobServiceTests.cs) |
| 开关缺失/矛盾、错误身份/地址、正常 TLS 与能力关闭 | `SourceIntakeStartupTests`、`SourceReturnStartupTests`、`SourceHandoffStartupTests`；[隔离运行](./IsolatedDeployVerification.md)与[部署](./Deployment.md) |

OSS 单图或全部不可用时，受管操作不报完成，保留日志、状态和对象，恢复后沿原操作推进。关闭相应处理能力停止新增/推进，但历史读取、完成重放和保护仍有效。Student 能力关闭时，旧退回在任何副作用之前 503；新退回按其能力开关与持久状态处理。错误凭据、未知回执、取消或提交确认丢失不能通过重试新操作、关闭认证或手工删历史绕过。

Portal 地址错误按实际 `MistakeService:Url` 排查。共享客户端对网络故障和未知下游响应产生安全失败，Portal 不能宣告审核或后续操作成功。健康检查的 live 成功只说明进程存活，ready 成功也不能代替业务完成或完整跨服务验收。

### TC-17：数据库启动自动升级与拒绝未知结构

保留原 TC-17 的升级、回滚和只读要求，具体支持版本与 catalog 由[迁移历史](../database/migrations.md)定义，不在本入口固定目标迁移数量。

- 空库、受支持的历史结构、旧运行时改名/误盖章形态和旧自动维护结构，正常启动自动升级；原行、ID、亲子关系及图片路径按迁移规则保留，需转换的原值进入受保护归档。重复旧条目不能为满足新唯一规则而丢弃。
- 已是目标的库以只读或无 DDL 权限账号启动且零写入；重复启动不写历史，并发启动只升级/归档一次。升级步骤与锁等待超过连接串命令超时仍能完成，宿主停止或取消仍中断并回滚。
- DDL 失败、核验失败、取消、断连或权限不足时整体回滚，原结构、数据、索引、约束与历史保持；诊断不包含行内容、连接串或密码。陌生旧表、未知/矛盾对象、错误类型、缺少保护或额外对象拒绝启动，不手工盖迁移章凑通过。
- 两个兼容升级命令复用正常启动流程；不要求 `--writes-stopped`/`--backup-verified`，显式 `--database` 与实际库不符仍拒绝。受保护归档或操作非空时按其规则拒绝 Down；备份恢复核对完整业务、来源、候选、操作、回执和 guards 后再续原操作，不只导出条目表。

实际测试为 [ReadOnlyStartupPostgresTests](../../src/Tests/Ruoyu.Study.Mistake.Service.Tests/ReadOnlyStartupPostgresTests.cs)、[LegacySchemaGuardPostgresTests](../../src/Tests/Ruoyu.Study.Mistake.Service.Tests/LegacySchemaGuardPostgresTests.cs)、[LegacySchemaConvergencePostgresTests](../../src/Tests/Ruoyu.Study.Mistake.Service.Tests/LegacySchemaConvergencePostgresTests.cs)，以及上表各操作的升级/恢复测试。前两类分别读取 `MISTAKE_READONLY_TEST_CONNECTION`、`MISTAKE_LEGACY_GUARD_TEST_CONNECTION`；旧结构收敛同用前者，均必须指向任务自有 PG 管理连接。备份命令与完整核对见[备份恢复演练](./BackupRestoreDrill.md)。

### 隔离启动与平台端到端

[07 隔离运行脚本](../../../../../script/build-script/07-mistake.isolated-deploy-verification.sh)与[运行说明](./IsolatedDeployVerification.md)涵盖基础 19 步、显式严格身份阶段和 Student-enabled 两模式。严格身份与 Student-enabled 阶段需要仓库外的受限 JSON：普通文件、非软链接、0600，正常 HTTPS、精确回调、CA 与真实身份提供者；Student-enabled 还需固定镜像和任务自有 provider 控制信息。缺少输入不能跳过相应阶段却报告整体通过。基础 ready 探测不证明真实 Student 业务调用成功。

平台端到端另从[现有 E2E 入口](../../../../../tests/e2e/README.md)运行，需要实际 Admin 托管会话与普通 CSRF。会话文件只含该 origin 的最小 Cookie，不导出下游 token。正常 TLS 与精确来源校验不能被关闭来凑通过。下节所述旧断言尚待修订，运行前须核对本次代码与案例是否一致。

## 4. 旧路径的实际限制与验收边界

未建立受管来源的旧创建、旧 Worker、旧 `complete-review` 和旧退回仍有兼容路径。旧 Worker/聚合审核可搬移图片并移除当前来源；旧退回先改审核、复制部分图片并删原 item/关联历史，再调用 Student。它使用固定 `mistake-return:{itemId}`，回调冲突或未知明确返回内层 false，但图片复制异常会被记录后继续，回调之前的删除不回滚；无成功副本时还可能跳过 Student 回调。旧聚合审核也可能在复制失败后返回业务成功。这些是当前限制，不能把它们列为正确的容错标准或认为重试已经可靠。

旧条目今后退回也须保存可恢复操作、失败可重试且不重复建 Student 记录，实施归 [IKIZY3](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY3)。在对应实现与验证交付前，不能把受管退回的保证套用于旧条目。已经缺失的历史记录不补建，不做物理清理；只读对账不会修复已丢载荷。

现有平台 E2E 01–03 的搬图、删当前记录、查询任意 Returned 记录和清理旧条目的断言，与受管永久保留/原操作回执之间仍有差额，修订及完整同版本联验归 [IKIZY6](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY6)。不得删除断言、放松 guards、恢复删原图或跳过故障场景来报告通过。此入口修订没有改变代码，也不表示这些端到端用例已经通过。

## 5. 执行与判定

按上述权威文档提供环境输入并执行对应专项；从仓库根运行快速验证：

```bash
./tests/integration/scripts/pre-commit.sh --skip-docker --skip-e2e --skip-integration
python3 tests/documentation/check_docs.py
git diff --check
```

受影响后端另运行对应 solution 的 Release build/test；真实提供方检查按各能力文档提供自有 PG/S3/HTTPS 输入。改前端时运行目标项目实际构建。完整平台 E2E、隔离运行、故障/取消/并发/恢复和实际消费者证据各自记录，快速检查不代替它们。

通过必须对应具体提交和每项成功证明；未运行、跳过、失败及结果未知都逐项保留。部分图片/引用缺失、错误授权、历史被删、未完成被显示为成功，均不接受为“部分通过”。按故障定位配置、依赖或实现，修复后重跑受影响场景；保留安全认证、原操作身份和永久数据保护。
