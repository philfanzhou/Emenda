# 不可变来源幂等入库

Worker 与两个 HTTP 创建入口共用 `ISourceIntakeService`。它把 Student 已取得的不可变来源物化为实际 MistakeItem、Created 日志和永久请求结果，保持 `SourceRetained`。[ADR-0016](../../../../../docs/adr/0016-mistake-retained-source-intake.md) 定义跨上下文主责和后继交接边界。

## 唯一语义模型

来源 manifest 的语义只引用 [Student SourceSnapshot](../../../ruoyu.student/docs/Integration/Mistake/SourceSnapshot.md)，不得改第二套 Path/Type/rotation/revision 规则。完整保存 sourceUploadId、StudentId、ContentRevision、SnapshotId、全部有序 images(path/type/length/sha256)、独立有序 rotations、acquiredAt/by。来源唯一 `(sourceUploadId,ContentRevision)`，同条件必须同 SnapshotId 与全 manifest；不允许客户端传 path/hash 来替代 provider。只对精确 `type=mistake` 条目创建业务组，非 mistake 图也保留在完整来源关系。

### 业务身份与合法分组

- 规范版本 `mistake-intake-v1`。区域 atom = 精确原对象键 + manifest 字节身份 + `Full` 或已有整数矩形 `(x1,y1,x2,y2)`。不转换 uploads/mistakes、不 lower-case、不 Unicode normalization、不以 UI/分页/VL 数组 index 做身份。manifest 位置只用于把 VL index 映射回稳定 atom；重复 exact path/bytes 的 full atom 不被当不同图片重复入库。
- 矩形采用现有整数坐标表示，要求 `0<=x1<x2`、`0<=y1<y2`；不擅自宣称现有协议有像素尺寸或归一化单位，不裁剪/变换图像。本切片不保证 box 在真实宽高内（provider 未提供尺寸），但 rotation/来源 revision 绑定，整数值原样保留。框相同才同 atom；不同合法矩形（含几何重叠）可独立建题，不能按 uploadId/原图粗糙唯一误杀合法裁剪。
- group key = 该来源 + 分类 mistake + 经 Ordinal 排序去重的区域 atom 集合的版本化、长度前缀无歧义编码；哈希只是索引，遇哈希碰撞仍比较 canonical bytes，不把 digest 当绝对身份证明。group 顺序/图片排序/VL重新编号不改变同一组；multi-image 合成一题保持一项结果；同 atom 不可同时归属于两个不同 group，重组共享 atom 返回 `grouping_conflict`，不静默复制。subject/grade/rootCause 是创建 payload，不是生成第二个业务 ID 的漏洞；同 group 不同创建 payload 明确 `payload_conflict`，后续正常编辑另用现有操作。
- 来源归属模式 first valid owner：Automatic / Manual 持久互斥。worker 先赢则一次完整自动分组已物化后冻结计划，重复不再调用 VL；手动随后相同 group+payload 可重放已有项，其余明确 `source_mode_conflict`。手动先赢则 worker 明确 manual-owned 并跳过，不自动补一条全图项；显式交接封口前手动可继续提交多个不同区域/多图组，遵守共同 atom 归属约束。一个手动请求对应一个 group；现有两个手动端点映射 full/cropped group 的同一业务键，不依端点改变键。合法组可能分多次手动提交，不能“首批整体封死”所有后继区域。
- automatic 组必须覆盖全部 distinct mistake full atom，各 atom 恰一次；VL omissions/重复/越界/坏类型不容许部分创建或以第二份 fallback 计划静默覆盖。VL 未配置时明确一次完整 fallback group（沿既有 subject=1,grade=7 与固定备注），作为已保存选择；配置但分析失败/不完整时保留可重试 claim、零新 items。canonical map 保存原显示顺序，规范排序只用于身份。

### 事件 × 产物 × 结果

| 事件 | 来源 intake / lease / group/result | HTTP/Worker结果与对象 |
|---|---|---|
| 新合法请求/轮询 | 核验/取得同 provider manifest；新 intake 持久完整关联、模式、owner UUID、fence=1、DB时钟 lease | 未物化不返回 Created；source永久pin，无外部删除 |
| 两实例/worker与手动抢同来源 | 唯一来源行 + 条件领取，首合法模式；未过期 lease 非owner不能处理 | busy/模式冲突；另一方不创建条目或搬图 |
| owner 分析中 | 不持 DB长事务；期限60s，DB条件heartbeat每20s；actor/request/group输入已绑定 | worker只对核验后的原图分析；lease失败停止提交 |
| lease过期/进程终止 | 新 owner 条件takeover，fence严格增加；保留manifest/已绑定请求 | 旧owner迟到分析或Save不能写；外部分析可能重叠但只有当前有效owner能提交 |
| 合法计划完成 | fresh context短事务锁source，检查owner+fence+lease，再保存组映射、items、每item一条Created log及请求结果 | COMMIT确认才200真实item IDs；自动全部组原子，不部分成功；source不移除 |
| 同业务组同创建payload、新request key | 查既有业务结果并原子绑定新alias | 相同item IDs，不增加log，不重跑VL |
| 同actor+action+requestKey同canonical payload | 原请求结果永久重放；不可用依赖不是重新入库理由 | 同IDs，含restart/HTTP丢失/停用新领取后的认证重放 |
| 同request key不同payload / 同group不同创建payload | 409且原关联不变 | 零新item/log、零OSS写/删除 |
| 事务前/中失败或取消 | rollback fresh context；已保存claim/manifest/request不能丢；lease可条件释放或自然过期 | 未完成；保留所有source，调用方取消传播 |
| COMMIT响应未知 | 不补偿删item/图，不判定rollback；fresh query同source/key | 实际Absent/claimed可重试，或同materialized重放；未知返回503 |
| 禁用新领取/重启 | 持久关联、业务键、guards与重放一直有效；停止新增/续领 | 不回落legacy，不清claim/manifest/items/pins |
| legacy insert/edit/delete/complete与managed source竞争 | 同来源序列化的DB guard及应用显式拒绝；不得删除/重指向managed identity/result | 409 `source_managed` / `handoff_pending`，在任何OSS/Student动作之前拒绝 |

lease release仅影响本域处理权，不释放Student snapshot或删除任何对象。自动组物化后仍保持 SourceRetained；完整封口/交接由[SourceHandoffs](./SourceHandoffs.md)推进，SourceRetained与HandoffCompleted可同时为真，无Released状态。

### 产物 × 持久关联

intake 保存完整 manifest、mode、owner/fence/lease、canonical group→稳定item ID/创建payload/保留来源状态；request replay 保存可信actor sub + action + UUID key、完整canonical request/digest、intake/group/result。映射引用实际 item 并RESTRICT删改，来源/业务身份/快照字段不可变，item owner/source/regions不可变，允许不破坏身份的subject/grade/rootCause正常编辑，但创建重放返回原创建结果而非重新生成。Created log 与item/result一起提交；来源/结果不能经TRUNCATE或直接SQL去关联，普通业务SQL旧写方同样被最终栅栏拒绝。旧其他item/practice/log数据保持。没有Student DB外键或直连。

### 入口 × 输入、信任与数据流

1. 既有 POST upload / Add：新增可选 `expectedContentRevision`、`requestKey`；开启新 intake 时二者为非零UUID必填，缺失明确400/无legacy fallback。source/student来自已核验manifest，请求student须完全一致。继承原AdminOnly及StaffOrAdmin矩阵，但新mode始终要求已验证受信issuer/audience/sub/role（不靠兼容false放行）；actor不是body/header自报。GET `/api/mistakes/intakes/{sourceId:guid}/{revision:guid}` 只读管理员结果/状态，始终认证及admin，返回不含凭据/签名URL的manifest关联与已存在IDs；未找到404、依赖/DB故障503，不成功空集合。
2. Worker：scan DTO/gateway透传已存在Student contentRevision；缺失/非法不领取，不用旧DTO Version替代。调用自有Student client的PUT/GET Snapshot，handler按精确guid路由+HTTP方法签发既有RS256的独立 `uploads.snapshot`，不透传最终用户token、不扩展任意path签发。校验201/200、完整字段/UUID归属/hash/length/类型/rotation；401/403、409/422、503、坏JSON、超时和取消分类，未知PUT只GET确认，不匿名降级。ordinary scan数据仅hint，不能覆盖manifest。
3. 核验原图读取：真实长度/hash与manifest匹配后才把这些原图交给已有VL；若字节missing/不符/中途故障，零新items、保留关联/claim，不将外部S3篡改当新版source。敏感原图仍按已有AI流程到配置的VL provider，本能力不新建其它外传；不日志原图、full hash、完整canonical payload、signedURL、JWT、密钥、student个人字段。日志只operation/source派生短ID、fence和固定阶段原因。
4. 新item可从现有item GET/list/review confirm/练习路径查询及使用；图片读取扩展现有presigned-url仅允许**与managed manifest和实际item关联完全匹配的原uploads或mistakes键**，始终认证+现有归属矩阵，来源不可用时失败关闭。new-source仅 `size=full`，不生成可变缩略图/不写OSS；任意uploads、错误大小写、跨student、未知source拒绝且零OSS。现有legacy mistakes路径行为保持。
5. managed item 的 delete/owner-source-regions修改、legacy complete-review、legacy Submit/Add 绕过新参数，一律在副作用前409；受管 reject 改走[SourceReturns](./SourceReturns.md)，关闭返回503 returns_disabled，开启后先持久意图并等待可信receipt，Pending用202/Success=false；Domain/repository/DB为共同栅栏，不能只endpoint提前检查。Confirm/metadata/练习原路径按既有授权正常，不能误报handoff完成。

### 实施任务 × 启用阶段

本服务实现来源入库，默认 `Mistake:SourceIntake:Enabled=false`；true启动须Student能力+服务签名+HTTPS以及strict API/resource auth全部有效，非法bool或不完整组合在DB/S3前失败。true worker整条改走新路径，永不继续旧copy/delete/remove；手动只接收新参数。false时legacy未managed记录兼容，显式新字段拒绝新领取503，已managed guards与认证GET/重放永久有效。

启用只在自有隔离实例验证。实际共享capture/rollout必须先停止所有旧Mistake版本direct movers/manualCompleteReview和外部破坏性StorageAudit；本服务不能阻止外部持S3权限的进程。后继正向handoff、条件ack/全引用/反向Saga完成且固定consumer升级以后才能阶段性取代旧流程；本能力不做该激活。


## HTTP 与失败契约

两个旧入口增加 `expectedContentRevision`、`requestKey` UUID；开启时必填且非零，body 的 studentId 必须与 manifest 完全相同。upload 仍要求 AdminOnly，Add 仍要求 StaffOrAdmin，actor 始终取已验证 sub。两个端点使用同一个 `create` 请求 action，JSON 属性顺序、端点名称和 full 图片排序不改变请求语义；null rootCause 与空字符串为同一创建值。upload 返回原 createdItemIds；Add 返回原创建 item DTO。

同一路由 upload 兼容可选 `sourceRegions`，复用既有 SourceRegionDto 与整数矩形。字段缺省或 null 仍将旧 `imagePaths` 映为 Full；显式非 null 必须为 1–100 个非 null 条目，精确非空路径和合法矩形，且 `imagePaths` 为 null/空数组。两种非空表达同时提供返回 400 invalid_request，显式空数组/非法区域返回 400 invalid_region；fractional/overflow 在 JSON binding 返回 400。非法区域在依赖读取/业务写入前拒绝。显式 regions 即使新领取关闭也要求原 managed revision/key、可信管理员认证，不能忽略 box 回落 legacy。bbox=null 的 Full 与旧 paths、Add 使用相同 create canonical 和原 IDs。

门户共享 Submit 签名和外部 Admin DTO 继续发送旧 paths，没有 crop UI 能力声明。新消费者发送 regions 前须证明固定 producer 发布的 v1 合同包含该字段；旧 binary 会忽略未知 JSON，不能安全接收 regions。默认回退保留当前 binary/schema/journal、关闭新领取；需要回到旧 binary 时须先停新 region 消费者。

新 GET `/api/mistakes/intakes/{sourceId}/{revision}` 始终 admin/authenticated，返回完整关联、模式、fence、leaseUntil、materialized、SourceRetained 与已存在 IDs。它不返回 JWT、签名 URL 或凭据。

| 原因 | HTTP / errorKind |
|---|---|
| 无受信主体 / 角色不符 / 学生不符 | 401 / 403；在来源读取或物化前拒绝 |
| 缺少、零 UUID、非法创建范围、非法整数矩形或图不属于 manifest | 400 / invalid_request、invalid_payload、invalid_region、region_not_in_manifest |
| 同 key 不同语义 / 同业务组不同创建值 | 409 / request_payload_conflict、payload_conflict |
| 活跃 lease / owner 失效 / 模式不兼容 / atom 重新分组 | 409 / intake_busy、lease_lost、source_mode_conflict、grouping_conflict |
| 来源已有 legacy item | 409 / legacy_source_conflict，包含原 item IDs；不合并或回填 |
| 受管来源旧写 / 删除 | 409 / source_managed、handoff_pending |
| 新领取关闭 | 503 / intake_disabled；已有同 actor/key 完成结果仍认证重放 |
| 来源或数据库不可用、坏 manifest/字节/VL、提交未知 | 503；固定阶段原因，无原始敏感数据 |
| 状态查询不存在 | 404 / intake_not_found |

快照 PUT 传输结果未知时只用同条件、同服务授权 GET 确认。提交后异常只 fresh-read 已存在结果；不能判定失败就删除 item 或对象。manual 创建日志与 group map、item、request IDs 同事务；automatic 所有组同事务。短事务才持行锁，外部字节/VL 阶段由独立 context 每20秒续60秒租约，数据库时钟判定。丢失续租即停止提交；未结束的非取消 OSS download 不能产生迟到写入。

## 兼容入口

关闭新领取且来源未受管时，旧 upload 每次创建一项，所有输入 path 成为 Full region；旧 Add 以所给 regions 创建一项。既有验证为 student/source 非空、subject 1–9、grade 1–12、非空图片/区域，状态 PendingReview，item 与一条 Created 日志同 Save。它们没有持久来源分组幂等；旧 Worker 的路径检查也不是并发、部分失败或跨资源安全保证。

受管读取和已完成 create 重放始终沿与严格 API policy 相同的 shared/default 与独立 Admin audience JWT schemes 验证 issuer、audience、签名和主体，不依赖 strict/resource/source-intake 开关。受管重分析 active/batch 查询以实际 job 的 item 关联识别来源；混合结果整体执行 staff/admin 与 reviewer=sub 检查，失败不伪装成功空集合。受管重分析写入的 studentId 必须匹配不可变来源 owner，在 HTTP 和领域入口写入前拒绝伪造值；历史非受管 jobs 不改写。

受管练习图片通过 practice→item→source group 的真实引用识别，精确区分来源 manifest 图片。owner learner 和既有 staff/admin review visibility 保持；staff 图片读取不绑定某个 reviewerId，job 查询仍绑定。来源图片继续校验 manifest 长度/摘要且只读 full；练习图沿现有图片读取与 size 语义，无伪造来源摘要。错误大小写或跨学生 learner 不能经关闭开关走匿名签名分支。

已受管来源永远不能回落到旧 create/mover。Confirm、metadata、既有练习和重分析仍可按原授权操作；delete、修改 owner/source/regions、旧 CompleteReview 在副作用前拒绝。受管 reject 只走[SourceReturns](./SourceReturns.md)；其意图存在后该 item 的 Confirm/metadata/practice/log/job 均阻止，其它 item 和后续合法 group 仍独立。DB guards 与不可变结果关联是旧 SQL/EF 写方的最后栅栏，未使用连接级绕过标志。

## 验证入口

`SourceIntakePostgresTests`、`SourceIntakeSecurityTests` 使用自有 PG15 随机库、实际 Student provider 正常 CA/SAN HTTPS 与 RS256、实际本地文件字节、独立 Mistake HTTP listener。覆盖成功/别名/禁用重放、三个入口、完整计划、模式/lease/fence、字节与协议故障、真实 commit 响应丢失、SQL guards、旧写竞争和真实 pg_dump/restore。它们是文件 OSS 替身验证，不是 live S3 发布证据。现有数据库升级、只读启动、身份和 Student 全回归继续约束邻接行为。

提供 `MISTAKE_INTAKE_TEST_CONNECTION` 与 `STUDENT_SCAN_TEST_CONNECTION` 为同一自有 PG15 admin 连接；backup 另需 `MISTAKE_INTAKE_TEST_PG_CONTAINER` 和 `STUDENT_SNAPSHOT_TEST_PG_CONTAINER`。全部测试使用随机库并清理；不得填共享业务库。

```bash
cd src/services/ruoyu.mistake/src
dotnet build Ruoyu.Study.Mistake.sln --configuration Release
dotnet test Ruoyu.Study.Mistake.sln --configuration Release
```

迁移结构见[来源入库表](../database/tables/source_intake.md)，启停/备份/恢复见[部署说明](../development/Deployment.md#来源入库启用与回退)，Student 与 OSS 交互分别见[Student](../Integration/Student.md)和[对象存储](../Integration/Oss/Oss.md)。

已提交组图片的独立目标复制、状态、恢复与安全读取见 [SourceTargets](./SourceTargets.md)。来源创建与结果重放仍保持原身份和 SourceRetained，不等于完整 handoff。
