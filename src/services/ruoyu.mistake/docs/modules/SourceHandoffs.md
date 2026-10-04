# 完整来源交接与永久受保护归档

Mistake 对完整封口的 source/revision 保存固定意图；Student 原子消费同一 acquired snapshot 的全部 mistake 位置并保存原历史回执；Mistake 只由可信历史 GET 和有效 source fence 的本域 Completed 提交确认完成。来源保持 SourceRetained，旧快照、原图、目标及全部业务历史永久保留。[ADR-0018](../../../../../docs/adr/0018-source-handoff-protected-archive.md)记录归档与条件消费的取舍，提供方契约见[SourceHandoff](../../../ruoyu.student/docs/Integration/Mistake/SourceHandoff.md)。

## 完整封口

封口与 Prepared 在同一 source advisory lock、行锁与现有 60 秒 lease/20 秒 heartbeat/递增 fence 下提交。没有第二套 lease。实际 group 集合非空且全部有真实 item/Created log；manifest 的每个精确 Type==mistake distinct 路径至少属于一个合法已提交组；所有 item 必须 Confirmed，或为具有原载荷可信回执的 managed SourceReturn.Completed 的 Rejected；所有涉及图片必须已选定 Verified targets。Automatic 还必须完整冻结计划 materialized。缺任一条件409，零新操作/封口/Student调用/OSS改动。

Manual 只有可信 Admin 显式封口，承诺本 revision 不再新增合法组，不证明 crop 覆盖全部像素。首批 targetReady 不封死后续组，多 crop 和后续图组仍可继续创建。封口后禁止新组、旧未完成 request 的新物化和 terminal 回退；已有原请求结果与同 canonical group alias 永久重放，不造新组。原 ItemIds=null request 保留但不承诺成功。其它来源独立。

固定 operationId 为 mistake-handoff:{snapshotId:D}，snapshot/source+revision/op 唯一。封口 actor 取可信 Admin sub 或 worker 固定主体，不从 body 输入；恢复者可不同但原 actor/payload 不替换。完整 groupsCanonical、目标 map、payloadCanonical 与 digest 原子持久化；完整 canonical 比较决定重放/冲突，digest 不替代身份比较。

## 固定编码

Encode 将每个字符串的 UTF-8 bytes 按 little-endian Int32 字节长度前缀拼接，再 Base64。Hash 是 canonical 字符串 UTF-8 的 SHA256 lowerhex。GUID 使用 lower-case D，长度是非负 invariant decimal，路径 Ordinal 且不归一化。

- groupsCanonical = Encode(mistake-handoff-groups-v1, 对 itemId Ordinal 排序的每个 Encode(itemId, 完整 group.Canonical))；groupsDigest=Hash(groupsCanonical)。
- payloadCanonical = Encode(source-handoff-v1, operationId, sourceD, revisionD, snapshotD, studentId, groupsDigest, Encode(对 sourcePath Ordinal 排序的每个 Encode(sourcePath,targetKey,length,sha256)))。

目标恰为全部 distinct mistake 路径，每路径一次，长度/hash 来自原 manifest；只能是 mistakes/source-targets/{sourceD}/{revisionD}/{nonzeroAttemptD}/original。Mistake 与 Student 分别实际 Download 全部固定目标并核验字节，不以存在性/客户端 length 代替证明。PG 使用原生 SHA256/UTF-8 编码，不引入扩展。

## 唯一恢复模型

| 状态 / 事件 | 持久结果与下一步 |
|---|---|
| 未封口且条件不足 | 409，未新增 handoff |
| 满足完整条件 | 同事务封口并 Prepared，固定事实不再变 |
| 已固定且可信 GET 404 | 先完整验证 targets，再 Sending，同 payload PUT，再可信 GET |
| PUT 201/200 或响应未知 | 不能推断完成；只读原历史 GET |
| receipt 完整匹配且 source fence 仍有效 | 原回执+Completed+完成时间同短事务提交 |
| 网络/TLS/取消/坏 JSON/401/403/404/5xx/字节失败 | 保留未完成与全部对象；重启同 operation 恢复 |
| 确定身份/载荷/receipt 不匹配 | 永久 Conflict；worker 不自动重试或解封 |
| late owner / lease 过期接管 | 递增 fence，旧 owner 最终提交失败 |
| Completed 或功能关闭后的完成重放 | 读原 durable Completed，无第二次消费 |

历史 GET 逐字段核对非零 receiptId、op/source/revision/snapshot/student、groupsDigest、v1/digest、UTC提交时间与原 recordDeleted/remainingImageCount/resultingContentRevision；当前记录后续合法编辑/删除不改变原回执。请求取消传播，不撤销已固定意图；IO 不持有 Mistake 长事务，短事务提交未知只 fresh read 确认，不删对象补偿。

同 source 后续真实新 revision 只在全部旧 managed revision durable Completed、历史学生一致时允许；旧 unmanaged item 或任一未完成 revision 仍409。旧来源、group、item/log、return/target/request 都不能删除或改身份。

## 管理入口与 worker

POST /api/mistakes/intakes/{sourceId:guid}/{revision:guid}/handoff 始终要求可信 Admin，不接收 paths/student/目标 body。只有 durable Completed 返回 HTTP200 data.success=true；待确认返回202 data.success=false、handoff_pending、来源交接处理中，请稍后重试。无效条件400、未知intake404、匿名401、非Admin403、条件/busy/conflict409、禁用新处理/依赖不可用503。

现有 Admin intake GET 增加安全 handoff summary（operationId/state/receiptId/completedAt/errorKind/sealed）；HandoffCompleted 只据 durable Completed，SourceState 仍 SourceRetained。不暴露 canonical/digest/目标 payload/签名 URL/服务凭据。

SourceHandoffWorker 启动及每20秒扫描 Prepared/Sending 和完整 Automatic，串行使用同一服务/lease；不自动封口未封口 Manual，能恢复已封口 Manual。默认 Mistake:SourceHandoffs:Enabled=false；true 要求 Student enabled、strict/resource、正常HTTPS与RS256，不要求 intake/targets/returns 三开关同时true。关闭只停止新增/推进，历史/guards/pins 永久有效。配置及显式 M9 升级/回退见[部署](../development/Deployment.md#来源交接启用与恢复)，表结构见[来源交接表](../database/tables/source_handoff.md)。

## 验证策略

SourceHandoffPostgresTests 覆盖完整分组、manual missing/多crop、实际 Admin/worker、故障恢复/取消/独立 lease 接管与新revision；SourceHandoffRecoveryPostgresTests 覆盖精确known1–8、只读catalog、双方所有阶段真实dump/restore和非空Down；SourceHandoffStartupTests 检查启动前约束。SourceHandoffS3Tests 的双方验证均连接同一任务自有真实 S3，独立正常 CA/SAN HTTPS、RS256、实际Copy/Download/consume/receipt和响应丢失。Student 的对应测试覆盖精确投影/历史/guards/认证/并发。

在仓库根运行 `dotnet build tests/integration/Ruoyu.Study.IntegrationTests.sln --configuration Release`，再执行 Mistake solution 的 portable 测试与 `dotnet test tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests/Ruoyu.Study.Mistake.Provider.Tests.csproj --configuration Release --filter FullyQualifiedName~SourceHandoff`；provider 与核心覆盖必须共同保留。专项环境变量沿[SourceReturns验证](./SourceReturns.md)和部署文档，必须任务自有 PG15/S3随机库桶，禁止共享业务库。没有前端产品变化；UI/fullE2E、共享消费者升级与物理GC不由本协议证明。
