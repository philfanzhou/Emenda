# Student HTTP 集成

Mistake 通过 HTTP 主动访问 Student，获取已分类的 Processing 上传记录，通知 Student 移除已处理图片，并在错题审核退回后创建 Returned 上传记录。`student_id` 是跨上下文引用；Mistake 数据库不对 Student 数据库建立外键。

## 边界与调用路径

Mistake Service 只依赖自有的 `IUploadSourceGateway` 与本地模型。Host 的 `StudentUploadSourceGateway` 将它映射到 Mistake 自有的 Student 出站客户端 `IStudentHttpClient`（Mistake 树内 `src/StudentClient` 库，IKJFF5 收拢，只暴露 Mistake 消费的四个端点面：scan / returned / remove-processed / accounts-students，语义与 JsonOptions、日志、异常分类自原共享实现逐字移植，wire 协议不变）。`UploadPollingWorker` 和审核端点使用同一个 scoped adapter 注册。地址由 Student 能力开关（`Mistake:Student:Enabled`，Local 缺省 `false`、Platform 缺省 `true`）决定：Local 启用能力时只接受显式 `Mistake:Local:Student:Url`，不从 `StudentService:Url`、`StudentService:BaseUrl`、`StudentService:GrpcUrl` 或 `http://localhost:5005` 回退；Platform 启用能力时仍按该顺序选择既有地址；能力禁用时客户端注册到保留的不可解析占位地址，本进程不会向 Student 发出任何请求。URL、JSON 和 HTTP 行为由该自有客户端控制；Mistake 构建不再依赖共享 `MistakeBff.HttpClients`（该共享库继续服务三个 Portal BFF 层，与本服务解耦，后续漂移由两侧协议测试矩阵约束）。配置细节见[部署说明](../development/Deployment.md)的“Student 能力开关与退回失败关闭”。

| Mistake 调用方 | 自有端口操作 | Student HTTP 客户端操作 |
|---|---|---|
| `UploadPollingWorker` | `GetProcessingUploadsAsync` | `ScanProcessingUploadRecordsAsync` |
| `UploadPollingWorker` | `RemoveProcessedImagesAsync` | `RemoveProcessedImagesAsync` |
| `MistakeHttpEndpoints` 审核退回 | `CreateReturnedRecordAsync` | `CreateReturnedRecordAsync` |

扫描映射保留记录 ID、Student ID、图片路径与类型、`hasMore` 和下一游标。worker 仅在成功读取一页并处理后使用下一游标；页间来源状态改变或删除不会使剩余项因 offset 左移而漏取。真正空页正常结束；HTTP 4xx/5xx、业务失败、网络故障、超时和坏 JSON 均作为故障停止本轮，下轮从新界限重试。调用方取消直接退出，不计为成功。移除结果保留成功标志、删除标志、剩余数量、错误消息与 `UnknownRecord` 分类（见「移除回调三态语义」）；退回操作返回分类结果（成功/载荷冲突/结果未知，见「退回回调幂等协议」）并携带 `operationId`、Student ID、图片路径和类型、备注与退回原因。adapter 复制可变列表，原样传递取消令牌及异常；不增加重试或缓存。

## 移除回调三态语义（IKJEY6）

`UploadPollingWorker` 的移除回调（Student `POST /api/uploads/{recordId}/remove-processed`）结果分三类，Worker 按类记录日志：

- 成功（含墓碑重放）：`RecordDeleted=true` 既可能是本次完整移除，也可能是 Student 依据删除墓碑 `upload_record_removal_tombstone` 对已完成的移除给出的幂等确认——两者都是终态确认，Worker 记 Info，不把重放成功当作新的移除动作。
- 未知记录（`unknown_record`）：记录不存在且无墓碑证明此前已完成。这是已迁移对象与 Student 记录之间的数据不一致，重试无法解决——Worker 记 Warning（含需人工核查来源记录语义），不重试；自动对账与修复入口属 IKJ0IO，本项不引入。
- 其他失败：保持既有 Warning 行为；传输异常保持既有 Error 行为。

自有客户端的 `RemoveProcessedImagesAsync` 透传 Student 响应的 `errorKind` 字段；`IUploadSourceGateway` 将其映射为 `RemoveProcessedUploadImagesResult.UnknownRecord`。存量已删除记录（墓碑引入前）重放按 `unknown_record` 返回，历史缺口由 IKJ0IO 对账处理。

## 退回回调幂等协议（IKJEY4）

审核退回（`POST /api/mistakes/{id}/review`，`ReviewStatus=Rejected`）创建 Student 退回记录时派生确定性幂等键 `operationId = "mistake-return:{itemId}"`，不引入 Mistake 侧持久化状态。回调结果分三类：

- 成功（含重放）：审核响应 `Success=true`；重放时日志记录 `replayed=true`，不视为失败。
- 载荷冲突（HTTP 409，同 `operationId` 异载荷）：审核响应 `Success=false` 并说明冲突，需人工核查 Student 侧退回记录。
- 结果未知（传输失败、超时或不可读响应）：审核响应 `Success=false`，错误文本含 `operationId` 与回查指引（Student `GET /api/uploads/returned/{operationId}`），先回查再以同一 `operationId` 重试。

自有客户端的 `CreateReturnedRecordAsync` 不吞 `HttpRequestException`：传输失败显式归类为「结果未知」并携带原始异常消息。审核端点自身的 reject 副作用（图片迁移回 `uploads/`、删除错题条目）发生在回调之前，不因回调失败回滚；已知失败不再被报告为审核成功。

## 当前失败语义

能力开关（`Mistake:Student:Enabled=false`）是第一层失败关闭：`UploadPollingWorker:Enabled=true` 与之矛盾并在启动前报错；`POST /api/mistakes/{id}/review` 请求 `ReviewStatus=Rejected` 时在调用领域服务、OSS 或 Student 之前返回 HTTP 503 与稳定响应（`Student 集成未启用，退回审核不可用`），不产生任何部分副作用，`ReviewStatus=Confirmed` 路径不受影响。

把查询故障降为空页的宽松 `GetProcessingUploadRecordsAsync` 只保留在 Portal 共享库 `MistakeBff.HttpClients` 中供 Portal 调用方使用；Mistake 自有客户端不携带该方法，worker 使用严格扫描方法。创建退回记录的回调已按「退回回调幂等协议」分类显式化（IKJEY4），传输失败不再被吞掉；移除回调已按「移除回调三态语义」分类（IKJEY6），墓碑重放幂等确认成功，无墓碑的未知记录失败且不静默重试。因此本切片不保证写回调与跨数据库、OSS 的原子性，Mistake 侧崩溃后的自动重试与补偿仍由 [迁出总览](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZXV) 的后续任务负责。先发布 Student 新端点和迁移，再发布本版 Mistake；反序部署时 Mistake 读取会显式失败，不回退到旧 offset 分页。

## 验证

adapter、HTTP 失败矩阵、移除回调 `errorKind` 透传（`RemoveProcessedImagesHttpClientTests`）、Worker 移除回调分类日志（`UploadPollingNotifyTests`）、退回回调分类（`CreateReturnedRecordHttpClientTests`）、reject 回调流程（`ReviewRejectCallbackTests`）、worker 游标与 Student 能力开关（含 503 失败关闭、矛盾配置与显式地址校验）测试位于 `src/Tests/Ruoyu.Study.Mistake.Service.Tests/`。在 `src/services/ruoyu.mistake/src` 运行 `dotnet build Ruoyu.Study.Mistake.sln --configuration Release` 和 `dotnet test Ruoyu.Study.Mistake.sln --configuration Release`。Student 的隔离 PostgreSQL 扫描测试需显式提供 `STUDENT_SCAN_TEST_CONNECTION`，只在临时数据库执行。

## 本地服务凭据

Host 的 `StudentServiceTokenHandler` 由同一个 typed client 管线用于 Worker、审核回调和 learner 账户归属解析；开启 `Mistake:Student:ServiceAuthentication:Enabled` 后每次请求签发新 RS256/60 秒服务 JWT 并覆盖请求 Authorization，不需要或转发用户 JWT。Worker scan / remove、审核 returned 和 returned 回查分别使用 `uploads.scan` / `uploads.remove` / `uploads.return`；账户查询改到始终认证的 `/api/integrations/mistake/accounts/{accountId}/students` 并使用 `accounts.read`。禁用时保留旧调用路径。

签发快照在启动时校验，默认要求 Student HTTPS 基地址；受控 HTTP 链路须显式 `AllowInsecureHttp=true`。签发失败、取消与超时不匿名重发；原业务游标、Returned 幂等/冲突/回查、墓碑/unknown_record 和错误分类保持原样。服务身份不会替代 Mistake 入站 resourceAuth 或 reviewer/student 授权。Student 的 [服务身份能力](../../../ruoyu.student/docs/Integration/Mistake/ServiceIdentity.md) 是 wire 合同权威入口；secret 文件挂载、启用与轮换见 [部署说明](../development/Deployment.md)。

## 来源快照消费

[来源入库](../modules/SourceIntake.md)开启时，scan/gateway 保留 ContentRevision，Worker 与两个手动入口通过正常 HTTPS 和独立 uploads.snapshot 的 RS256 服务 JWT 获取同条件 PUT/GET。签发 handler 仅接受精确 GUID snapshot 路由及 GET/PUT，坏响应/未知结果不匿名降级。scan 的 StudentId/path/type 只是 hint，创建与 VL 字节依已核验完整 manifest。关闭后已有 guards、只读状态和认证请求重放仍有效。

当前新条目保持 SourceRetained；这条调用链没有 copy/delete/remove/ack/release。旧未受管 Worker/CompleteReview 仍是旧流程，后继 durable handoff/条件确认/全引用另归父任务，禁止两者混用。提供方来源与 pin 规则只有 [Student SourceSnapshot](../../../ruoyu.student/docs/Integration/Mistake/SourceSnapshot.md) 一份权威，不在客户端另立 revision/path 规则。
