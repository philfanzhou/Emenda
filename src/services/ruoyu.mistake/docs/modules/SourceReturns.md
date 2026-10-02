# 受管错题持久退回

[SourceIntake](./SourceIntake.md) 的实际 group→item→source 是唯一输入，使用本组已经 [Verified 的目标](./SourceTargets.md)。一个 PendingReview item 对应一个永久操作 `mistake-return:{itemId:D}`；成功同时需要 Student 原载荷的[可信历史回执](../../../ruoyu.student/docs/Integration/Mistake/ReturnedReceipt.md)和本域原子 Completed/Rejected/最终日志。来源、创建重放、group、item、practice、所有日志和 source/target pins 保留；SourceRetained、handoffCompleted=false 不变。[ADR-0017](../../../../../docs/adr/0017-managed-mistake-durable-return.md)说明主责与取舍。

## 操作与图片

先在来源 advisory lock 和现有 60 秒 lease/20 秒 heartbeat/递增 fence 下持久 Prepared 意图。冻结实际 item/group/source/revision/snapshot/student、可信审核主体、归一请求、原元数据和有序 full 原图集合。相同 item/canonical 重放同操作；不同 actor/reason/rootCause/有效 grade 或 subject 返回409 return_payload_conflict。reason null 与空串等价，rootCause null 与空串不同；有效范围外的 grade/subject 按既有审核规则忽略。body id 不覆盖路由 id。active Pending/Running reanalysis（含过期 Running）、非待审核 item、缺少 Verified targets、来源 busy 都在新意图和 IO 前拒绝。

按 group regions 原显示顺序精确路径首次去重，同图多 crop 仍退回一张完整原图，多图为同一 Student 记录。源来自该来源实际选定 Verified target，Download 长度/SHA256 与 manifest 相符后才复制，不裁剪、不旋转。每次新 reserve 一个随机 attempt 和唯一键：

```text
uploads/mistake-returns/{itemId:D}/{attemptId:D}/original.jpg
```

reservation 先提交，键只写一次。正常 await Copy 完成且有效 owner/fence 持久 CopySettled 后，Download 校验成功才选定。无 settled 标记的失败、取消、响应丢失和旧 owner 候选，即使字节相符也永不选定；恢复创建新键，晚到非取消 Copy 只能写旧未公开键。settled COMMIT 响应未知时仅 fresh read 的实际标记允许复用。所有候选永久保留，不删除补偿。

选定齐备后，ordered imageEntries(type=mistake)、student、comments=Rejected、reason、operationId 与 returned-payload-v1 摘要一起保存并进入 Sending。路径/顺序/类型/元数据之后不替换、不重写、不再造第二 payload。数据库使用同一 UTF16 长度前缀规则和 PostgreSQL 原生 SHA256 核验 digest，没有扩展依赖。

## 跨域确认与恢复

只调用既有 Student POST，同 operation、固定 payload；先查询专用历史 receipt，匹配时直接完成本域，不重新创建已合法编辑、消费或删除的 Returned Record。404 仅表示查询时没有已提交回执；再次 POST 前核验固定选定字节仍可用。POST 200/Success/Replayed 不证明本域成功，POST 后须重新 GET receipt。

receipt 必须来自正常 TLS 和 RS256 uploads.return 服务身份，核对 operationId/student/非零 recordId/合法 UTC 时间/returned-payload-v1/原 digest。401/403、503、断开、超时、坏 JSON 和取消均不完成、不匿名降级；确定身份/载荷冲突持久 Conflict 并停止自动推进。匹配后在当前 lease/fence 短事务内同时保存 receipt、Completed、item Rejected/审核元数据和恰一条 ReviewRejected 日志。延期完整性检查禁止部分 final COMMIT；响应丢失仅 fresh read 的 durable Completed 可报成功。

`SourceReturnWorker` 启动及每20秒串行扫描数据库 Prepared/Sending，使用同 source lease 恢复。请求取消停止本请求推进、传播取消，不撤销已持久意图；后续 worker 可继续。所有 IO 在短事务外。禁用停止新增/推进，不清理状态，已有完成重放、授权读取及最后 DB guards 永久有效。

## HTTP 与观察合同

沿既有 `POST /api/mistakes/{id}/review` 的 Rejected 分支，受管数据始终执行可信 JWT、StaffOrAdmin、reviewerId=sub 和实际归属。legacy 未受管流程及 Confirm 兼容。受管数据不进入先删除的 legacy reject。

- durable Completed：200，旧 envelope 内 data.success=true，附可忽略 operationId/returnState/confirmedRecordId；removedImagePaths 为空。
- 已接受且尚在推进：202，data.success=false，errorKind=return_pending，errorMessage=`退回处理中，请稍后重试`。
- 输入400、未找到404、认证401/403、确定冲突409、依赖/未知结果503，均不能以成功 HTTP 状态代替业务成功。

共享 MistakeHttpClient 读取 data.Success；共享 MistakeReviewController false 时传播中文 ErrorMessage。Teacher/Assistant 共用 ReviewDetail 仅 result.success=true 才显示“已退回”及 emit reviewed/close。intake 管理查询附安全 return summary，只有 itemId/operationId/state/confirmedRecordId/completedAt/errorKind，没有 payload、reason、digest、候选键。

默认业务 list/by-upload/pending counts 排除 Completed tombstone；授权 item GET 与显式 Rejected list 保留历史。create replay 永久返回原创建事实。return 意图阻止同 item 的 Confirm、metadata、practice、任意 log 和 reanalysis enqueue/claim/complete；普通 SQL/旧 EF 的最后门禁拒绝绕过、删除或复活。其它 item 和合法后续 group 不被冻结。

## 启用与保证边界

`Mistake:SourceReturns:Enabled` 默认 false，只接受 bool。true 要求 Student、strict API、resource auth、有效 RS256 签发与 HTTPS，在 DB/S3 初始化前失败关闭。无需同时启用新的 intake/targets 领取，只使用已有 Verified 事实。数据库结构、完整备份与 known1–7 升级见[来源表](../database/tables/source_intake.md)与[部署](../development/Deployment.md#受管持久退回启用与恢复)。

没有跨 PG/S3 原子提交、即时停止非取消 IO、legacy 异常修复、完整 forward/ack/remove/release、fullrefs 或 GC 保证。退回图是 Student 可编辑正常 uploads，后续合法 COW/删除/再分类可以发生，历史选定关联不授权重写或回收。完整 Storage Audit 合同前必须隔离旧 movers 和外部破坏性 GC；独立 DB/OSS 管理凭据不在应用保证内。

## 验证入口

实际 PG、正常 CA/SAN HTTPS/RS256 Student 与独立 Mistake HTTP、提交前故障/真实提交后响应丢失、取消/晚到 Copy、SQL/授权/恢复见 `SourceReturnPostgresTests`。真实 S3 Copy/Get 与同提供方联合链路见 `SourceReturnS3Tests`。提供任务自有随机 PG/S3，不能使用共享业务库；跳过不代表通过。

```bash
cd src/services/ruoyu.mistake/src
dotnet build Ruoyu.Study.Mistake.sln --configuration Release
dotnet test Ruoyu.Study.Mistake.sln --configuration Release --filter 'FullyQualifiedName~SourceReturn'
```

共享消费回归：`MANAGED_RETURN_BFF_EVIDENCE=<evidence-dir> dotnet test src/ruoyu.common/test/Ruoyu.Study.MistakeReviewBff.Shared.Tests/Ruoyu.Study.MistakeReviewBff.Shared.Tests.csproj -c Release --filter FullyQualifiedName~ActualHttpClientPreservesManagedPending` 通过实际 HttpClient 与共享 Controller 保存 pending/Completed BFF JSON。随后 `node tests/integration/scripts/test-managed-return-ui.cjs <evidence-dir> [frontend-dependency-dir]` 用已有 Vue/TypeScript 依赖编译并执行实际 ReviewDetail 的提交方法，断言中文提示与 reviewed/close 事件；无新 UI 或依赖。
