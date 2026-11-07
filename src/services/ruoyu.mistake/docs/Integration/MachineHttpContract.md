# Mistake 版本化 HTTP 合同

[mistake-http-v3.json](./mistake-http-v3.json) 是当前 `/api/mistakes` 业务 wire 的唯一机器规格：OpenAPI 3.1、JSON Schema 2020-12 与显式 `x-ruoyu` 条件语义。人读文档不另建字段 catalog；旧 [Portals 文档](./Portals/01-FEATURE.md) 中已标注过期的 gRPC 叙述不能用作当前 HTTP/managed 模式的 oracle。其他 namespace 由规格中的 `otherNamespaces` 单独登记；`/api/integrations/storage-audit/references` 曾属 StorageReferences v1，该端点已按 ADR-0030 第 4 步删除；不可变的 v3 规格仍登记这一 namespace，但服务不再提供它。

## 取得发布版本

匿名 `GET /contracts/mistake-http-v3.json` 返回发布资源的精确 UTF-8 bytes，`Content-Type: application/json`、以内容 SHA256 为值的带引号 ETag、`Cache-Control: public, no-cache`。`If-None-Match` 支持 weak tag、列表与 `*`；命中返回空 body 的 304。未知版本返回 404，取消请求尊重 RequestAborted。入口在业务 `/api` 组之外，compatibility、strict、resource 模式均可读取，不读取 DB、Student、OSS、当前用户或运行配置，不提供带凭据或真实对象的示例。

规格嵌入 `Mistake.Service` 发布程序集；Host 从该资源返回内容，Dockerfile 显式带入源 artifact。运行时无需 checkout、文档目录或特定工作目录。共享客户端也嵌入同一源 artifact，并仅 source-link 不依赖 Domain/Database 的纯 schema 校验器；它不引用服务 DTO 或服务项目。

```bash
# 使用部署者已信任的正常 CA；不要关闭 TLS 校验。
curl --cacert '<CA-file>' --dump-header headers.txt \
  'https://<owned-host>/contracts/mistake-http-v3.json' -o contract.json
sha256sum contract.json
```

`x-ruoyu.ops` 将健康探针与业务路由分开：live 无依赖；ready 按固定启动快照检查 DB、OSS、已启用 Student、已配置 VL；strict 启用时还要求 issuer Discovery 与精确配置匹配、同源 JWKS 非空。任一必需组件失败返回503，spec取得仍独立可用。

hash 是发布身份，不是业务权限。回滚规格发布不改变原 schema、开关默认值、认证、历史 guards 或来源 pins。破坏性改动使用新版本并原样保留历史发布 artifact，不能用同名 v1 覆盖旧消费者所需字段。v2 根据用户批准取消旧匿名/先删除退回，统一可信审核身份与持久完成语义，新增 Admin 六字段状态 GET 和 Capturing 状态。原 `/contracts/mistake-http-v1.json` 精确 bytes/hash 保留，响应 `X-Ruoyu-Contract-Status: historical` 与 latest-version Link；它只供历史核对，不表示危险旧写入仍可执行。当前 v3 的共享客户端与三个 Portal 同步消费当前合同；这里不要求先部署共享环境或人工登录。

## 按真实结果消费

各 operation 的参数、请求与 response schema 以及 `x-ruoyu.authorization/semantics` 一起构成合同。请求 schema 描述实际 binding 能接受的字段、nullable/default；业务合法性由相应条件描述，非法值可能得到 400、409 或现有业务失败，不能把 schema 合法当作业务已完成。

- 普通 envelope 的外层 `success=true` 只表示成功适配，内层 `success=false` 仍是业务失败，例如缺失项 DELETE 或 legacy complete-review。
- 受管 review/handoff 的 202 具有外层 true、内层 false，表示可恢复的 pending；只有 durable Completed 的 200/内层 true 才成功。SourceRetained、targetReady、handoffCompleted 是不同事实。
- intake GET/targets/handoff 三个入口在 compatibility/strict/resource 各模式均要求已认证 Admin subject，无匿名兼容窗口；历史 v1 的条件匿名权限不得套用到它们或当前 Rejected 分支。
- intake GET/targets 使用 raw summary，包含 nullable 安全 receipt 状态，不能按 envelope 解析或公开 canonical payload。
- 版面检测受管 start/status（`POST`/`GET /api/mistakes/intakes/{sourceId}/{revision}/layout`，IKQA2P additive 登记）沿用 intake 家族授权：始终要求已认证 Admin subject，无匿名兼容窗口。start 携带 UUID `requestKey`：同 caller/action/source 同键重放同一作业与其捕获的非秘密配置，冲突输入 409 `request_payload_conflict`，新键产生下一个独立 retained result revision；排队作业立即返回（接受推理是 durable worker 的工作，不是该次 HTTP 交换）；无 eligible mistake 图像 400 `no_layout_images`、未受管来源 404 `source_not_managed`、能力关闭拒绝新工作 503 `layout_disabled` 而同键精确重放保持可用。status 返回 raw durable 视图（每图状态与有界 attempts、接受结果的 validated regions 与捕获配置）；任何响应不含凭据、凭据值或签名 URL，能力关闭时读取与既有结果保持可用。
- Source manifest 的 `type` 是必需的原始 metadata string，保留 homework/custom 等非 mistake 值；只有 mistake 类型具备入库资格，不把该字段伪造为只允许 mistake 的封闭 enum。
- upload 的可选 `sourceRegions` 是同一路由的兼容添加：absent/null 保持旧 paths Full，显式区域与非空 paths 互斥，并始终要求原 managed 条件和认证；同 canonical 的 Full/crop 沿原请求结果重放。现共享 Submit 和 Admin 消费者仍使用旧 paths。新消费者仅在固定 producer 发布资源证明字段存在后启用；关闭新领取保留历史重放，不把旧 binary 忽略未知字段当安全回退。
- 应用 message 错误与 intake `errorKind/conflictingItemIds` 不统一；framework binding 400、constraint 404、method 405 与 default server/transport 错误可能是 empty、ProblemDetails 或非 JSON。客户端不可据状态码或 DTO 初值猜测成功。
- presigned URL 是短期 bearer 能力，不记录活 URL；实际 auth、Ordinal 前缀、full-only、size/expiry 与存在性语义按该 operation 的条件执行。

共享 MistakeHttpClient 的 14 个方法按固定版本校验实际 response，并共用 [Portal 安全失败/取消边界](../../../../ruoyu.common/docs/bff-shared/MistakeBff.HttpClients.md)。合法 200/Review 202、空列表、详情实际 404、Delete/legacy complete-review 内层 false 和 managed pending 形状保留。合法应用 400/409 映射到相同 Portal 状态码与固定安全提示；其他认证拒绝、未知状态、ProblemDetails、空/非 JSON 或 schema/type/enum/矛盾 pending、传输未知和非调用方 timeout 映射安全 502。provider 401 不等于 Portal 登录 401，不 retry/fallback，也不把失败包装成成功空列表。

异常/InnerException、响应与日志不得保留远端 body/message、原异常、URI、token/Cookie 或 signed URL。调用方 CancellationToken / RequestAborted 在预取消、发送与读取阶段透传，停止后续 I/O；未知响应不证明未提交，取消不承诺回滚。真实 wire 对不安全方法验证最多一次发送；DELETE 保持零字节 payload 并避免 bodyless 请求的底层 EOF 重发。所有实际 Portal Mistake action 消费同一分类；聚合页取数失败立即终止完整成功页，后续 Student 写入受前置 Mistake 成功约束。

v3 只启用现有 review 的旧题恢复可用性：合法 LegacyReturns=true 可持久接受 Capturing/Prepared/Sending，202/data.success=false；只有 durable Completed 才200/data.success=true。false 仍只读禁用，新/未完成503、确定 canonical 冲突409、Completed 精确请求认证重放200。输入、schema、路由/方法、状态 enum、资源权限和 Student wire 与 v2 精确相同。检查器对 v2→v3 重构唯一允许的文档并逐成员相等比较，仅豁免 review 的两个精确 semantics 值和发布版本 metadata，不能用整个 operation 的 changedLocations 掩盖其它漂移；原 v1→v2规则保留。

历史 [v1](./mistake-http-v1.json) 和 [v2](./mistake-http-v2.json) 的原 bytes/hash/ETag/304 与历史标记继续发布，latest-version Link 指向 v3；历史合同用于核对当时行为。共享客户端与三个 Portal 嵌入当前 v3，14 方法的原 DTO/wire 不变。停止新退回应关闭相应开关并保留当前 binary/schema/永久 pins；不得恢复匿名、先删除或放松 producer guards。

## 可执行检查

```bash
python3 tests/contracts/check_mistake_http.py --baseline-ref origin/master
python3 tests/contracts/test_mistake_http.py
# 必须基于审查过的 additive 判断，并同步实际 provider/consumer fixtures；不能放行 breaking。
python3 tests/contracts/check_mistake_http.py --baseline <released-v1.json> --allow-additive

dotnet test src/services/ruoyu.mistake/src/Tests/Ruoyu.Study.Mistake.Service.Tests/Ruoyu.Study.Mistake.Service.Tests.csproj \
  --configuration Release --filter FullyQualifiedName~MistakeHttpContract
dotnet test src/ruoyu.common/test/Ruoyu.Study.MistakeBff.HttpClients.Tests/Ruoyu.Study.MistakeBff.HttpClients.Tests.csproj --configuration Release
dotnet test src/ruoyu.common/test/Ruoyu.Study.MistakeReviewBff.Shared.Tests/Ruoyu.Study.MistakeReviewBff.Shared.Tests.csproj --configuration Release
# 实际 producer 模式与已注册 Portal 的 DI/HTTP、RequestAborted 边界。
dotnet test tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests/Ruoyu.Study.Mistake.Provider.Tests.csproj \
  --configuration Release --filter FullyQualifiedName~MistakeConsumerHostModes
dotnet test src/user_portal/backend/Tests/WebApi.Tests.csproj --configuration Release --filter FullyQualifiedName~MistakePortalBoundary
dotnet test src/teacher_portal/backend/Tests/TeacherWebApi.Tests.csproj --configuration Release --filter FullyQualifiedName~MistakePortalBoundary
dotnet test src/assistant_portal/backend/Tests/AssistantWebApi.Tests.csproj --configuration Release --filter FullyQualifiedName~MistakePortalBoundary
```

`pre-commit.sh` 接入 vocabulary/diff/独立 CLI mutation 检查；它要求可读的 `origin/master` 基线；新版本当前 artifact 不存在时，先比较声明的上一已发布 artifact，并检查历史 bytes 原样保留。仅真正没有当前/上一 artifact 才记录 initial_version。版本升级必须声明真实 previousVersion 和逐项 changedLocations；同版本 breaking 永远拒绝。已批准的 additive 变更可给验证脚本设置 `MISTAKE_HTTP_ALLOW_ADDITIVE=true`；同版本 breaking 仍拒绝；新版本仅允许已登记的破坏性位置，其它漂移拒绝。v3 后续已审查的 additive 演进（版面受管 start/status，IKQA2P）沿用同一门禁；对历史 v2→v3 注册转换的重构等值检查在 `--allow-additive` 下只豁免纯 additive 漂移（`differences` 全量分类，无 breaking 项），转换外壳——paths、schemas、派生操作计数与转换元数据之外的一切——仍须逐成员相等。runtime EndpointDataSource 与 spec path/method 集合 exact 对比发现新增/删除业务路由，其他公开 namespace 必须登记主责。发布 bytes/hash、匿名/conditional GET、无业务注册的取消、实际 provider binding/serialization 及 14 concrete consumer 方法分别测试；独立 mutation 对 route、required、type、enum、security、error 缩减必须以 exit 1 失败。

本地 InMemory/OSS doubles 的 wire 测试与真实 PG/S3/正常 CA/提供方 token 验收分开记录。协议业务演练同时覆盖当前来源同 key 重放/异 payload、lease/fence、封口、新 revision、受管退回与交接响应未知及历史恢复、reanalysis Pending/Running；沿现 [SourceIntake](../modules/SourceIntake.md)、[SourceReturns](../modules/SourceReturns.md)、[SourceHandoffs](../modules/SourceHandoffs.md) 验证入口。总数、schema 语法或 spec 可获取都不能单独证明业务验收通过。
