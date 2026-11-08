# 保留来源的图片目标物化

已有 [SourceIntake](./SourceIntake.md) 的 source、已提交 group 与真实 item 是唯一输入。按这些组实际引用的精确原图路径去重，不复制 manifest 中未被引用的图片。原 SourceRegions、canonical、manifest 与 create replay 保持不可变；同图多区域、多组与后来新增合法组复用同一目标。来源保持 `SourceRetained`，本能力不调用 Student ack/remove/release，不删除来源或候选。

## 持久处理与恢复

每个 `(sourceUploadId,contentRevision,sourcePath)` 有一个 `source_target_object`，绑定原 snapshot/student 与预期长度/SHA256。先提交 object/pin 与 `source_target_attempt` reservation，再调用实际 `IOssService.CopyObjectAsync`。每次尝试只写自己的新键：

```text
mistakes/source-targets/{sourceUploadId:D}/{contentRevision:D}/{attemptId:D}/original
```

复用来源级 PostgreSQL advisory lock、60 秒 lease、递增 fence 与 20 秒 heartbeat；不另建租约。候选经实际 Download 校验完整长度/SHA256后才记录 Verified，当前合法 owner/fence 才能发布单一选定映射。选定不可替换，目标不写回原图路径。

Copy 不支持取消或条件创建。取消、超时、响应丢失与进程停止均可能留下 Reserved/Unknown/Copied 候选；它们仍由数据库保存并保护。恢复只 Download 已知候选并校验、用当前 fence 发布，或先预留一个新键再复制；不重写任何已有候选。晚到 owner 最多写自己的键，旧 fence 不能发布，也不能覆盖另一选定键。DB COMMIT 结果未知时 fresh read 确认持久事实；不做删图补偿。

`SourceTargetWorker` 启动后及每 20 秒扫描数据库已提交组的缺失目标，串行恢复。失败保留状态并在后续扫描重试；管理员也可请求同一个恢复流程。关闭 source intake 或 Student capture 不阻止已保存来源恢复。没有即时取消非取消型 OSS 请求、跨 DB/S3 原子提交、候选回收或完整双向 handoff 的保证。

## HTTP 与权限

`POST /api/mistakes/intakes/{sourceUploadId}/{revision}/targets` 无自定义路径/hash/student 请求体。GET 同一 intake 路由与 POST 始终只允许有效 Admin；与严格 API 相同的 JWT schemes 验证 issuer/audience/签名/有效期/sub。未知来源 404、并发持有或确定冲突 409、能力禁用/依赖不可用/结果未知未收敛 503；尚未齐备不返回成功。

GET 增加 `targets`（SourcePath/State/TargetKey）、`targetReady` 与 `handoffCompleted`。仅选定 Verified 公开 TargetKey；targetReady 是当前已提交组图片集合齐备，合法新组可能使它重新 false；handoffCompleted 仅由[SourceHandoffs](./SourceHandoffs.md)的durable Completed决定；单独targetReady不证明完成。SourceState 恒为 SourceRetained，不作为来源释放或 GC 授权。

选定目标通过已有 `POST /api/mistakes/presigned-url`，`size=full` 读取，签名前再次 Download 核验长度/SHA256。精确目标→来源组→真实 item→student 决定归属，沿用 learner 本人、Staff 审核可见性、Admin 矩阵；learner 学生范围解析失败 503。其他 size、未知/待定/失败候选、错误大小写或失配字节均失败关闭；原图读取继续有效。关闭任意能力开关不降低受管读取的身份与归属校验。

`mistakes/source-targets/` 与 legacy 换前缀可能产生它的 `uploads/source-targets/` 由共享 reserved 判定保护。旧 Worker、CompleteReview、reject 的源和计算目标均在副作用前拒绝；item/practice/source HTTP/领域写入及 DB guards 拒绝伪造新增 namespace 的 legacy 引用。来源与 item/group 关系继续受既有不可变 guards 保护。持有独立 OSS 凭据的外部 writer/GC 不在本接口控制范围，部署者必须禁止其触碰这个 namespace。

## 配置、数据库与验证

`Mistake:SourceTargets:Enabled` 默认 false，仅接受 bool；true 要求 strict API authentication 与 resource auth 有效，启动时验证已有身份信任配置。无新服务、依赖、Student token 或接口；既有 PostgreSQL 与 OSS 承载处理。关闭只停止新 copy，不撤销安全读取、状态及 guards。

[来源表](../database/tables/source_intake.md)描述 object/attempt 的 restrict 关系、唯一键、永久 pin、选定与 fence guards；[部署](../development/Deployment.md#图片目标启用与恢复)定义启动时自动升级、备份/恢复和回退。

测试位于 `SourceTargetPostgresTests` 和 `SourceTargetS3Tests`：实际 PG、实际提交响应丢失、取消/晚到 owner、SQL 保护、六历史非空升级、pg_dump/restore、严格 HTTP/full 下载；S3 用当前 S3OssService 的实际 Copy/Download 和任务自有 S3 bucket。只提供专用 PostgreSQL admin 连接和专用 S3 实例，fixture 创建随机数据库/bucket；不得填共享业务环境。配置 `MISTAKE_INTAKE_TEST_CONNECTION`、`STUDENT_SCAN_TEST_CONNECTION`、`MISTAKE_INTAKE_TEST_PG_CONTAINER` 与 `MISTAKE_TARGET_TEST_S3_ENDPOINT` 后运行：

```bash
# Run from the repository root; provider and portable suites are both required.
dotnet build tests/integration/Ruoyu.Study.IntegrationTests.sln --configuration Release
dotnet test src/services/ruoyu.mistake/src/Ruoyu.Study.Mistake.sln --configuration Release --filter 'FullyQualifiedName~SourceTarget'
dotnet test tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests/Ruoyu.Study.Mistake.Provider.Tests.csproj --configuration Release --filter 'FullyQualifiedName~SourceTarget'
```
