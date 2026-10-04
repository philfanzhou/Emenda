# Mistake 版本化 HTTP 合同

[mistake-http-v1.json](./mistake-http-v1.json) 是当前 `/api/mistakes` 业务 wire 的唯一机器规格：OpenAPI 3.1、JSON Schema 2020-12 与显式 `x-ruoyu` 条件语义。人读文档不另建字段 catalog；旧 [Portals 文档](./Portals/01-FEATURE.md) 中已标注过期的 gRPC 叙述不能用作当前 HTTP/managed 模式的 oracle。其他 namespace 由规格中的 `otherNamespaces` 单独登记；本版本不包括未来 StorageReferences 或物理 GC。

## 取得发布版本

匿名 `GET /contracts/mistake-http-v1.json` 返回发布资源的精确 UTF-8 bytes，`Content-Type: application/json`、以内容 SHA256 为值的带引号 ETag、`Cache-Control: public, no-cache`。`If-None-Match` 支持 weak tag、列表与 `*`；命中返回空 body 的 304。未知版本返回 404，取消请求尊重 RequestAborted。入口在业务 `/api` 组之外，compatibility、strict、resource 模式均可读取，不读取 DB、Student、OSS、当前用户或运行配置，不提供带凭据或真实对象的示例。

规格嵌入 `Mistake.Service` 发布程序集；Host 从该资源返回内容，Dockerfile 显式带入源 artifact。运行时无需 checkout、文档目录或特定工作目录。共享客户端也嵌入同一源 artifact，并仅 source-link 不依赖 Domain/Database 的纯 schema 校验器；它不引用服务 DTO 或服务项目。

```bash
# 使用部署者已信任的正常 CA；不要关闭 TLS 校验。
curl --cacert '<CA-file>' --dump-header headers.txt \
  'https://<owned-host>/contracts/mistake-http-v1.json' -o contract.json
sha256sum contract.json
```

`x-ruoyu.ops` 将健康探针与业务路由分开：live 无依赖；ready 按固定启动快照检查 DB、OSS、已启用 Student、已配置 VL；strict 启用时还要求 issuer Discovery 与精确配置匹配、同源 JWKS 非空。任一必需组件失败返回503，spec取得仍独立可用。

hash 是发布身份，不是业务权限。回滚规格发布不改变原 schema、开关默认值、认证、历史 guards 或来源 pins。破坏性改动使用新版本并保留原路径与兼容窗口，不能用同名 v1 覆盖旧消费者所需字段。

## 按真实结果消费

各 operation 的参数、请求与 response schema 以及 `x-ruoyu.authorization/semantics` 一起构成合同。请求 schema 描述实际 binding 能接受的字段、nullable/default；业务合法性由相应条件描述，非法值可能得到 400、409 或现有业务失败，不能把 schema 合法当作业务已完成。

- 普通 envelope 的外层 `success=true` 只表示成功适配，内层 `success=false` 仍是业务失败，例如缺失项 DELETE 或 legacy complete-review。
- 受管 review/handoff 的 202 具有外层 true、内层 false，表示可恢复的 pending；只有 durable Completed 的 200/内层 true 才成功。SourceRetained、targetReady、handoffCompleted 是不同事实。
- intake GET/targets/handoff 三个入口在 compatibility/strict/resource 各模式均要求已认证 Admin subject，无匿名兼容窗口；legacy 入口的条件匿名权限不得套用到它们。
- intake GET/targets 使用 raw summary，包含 nullable 安全 receipt 状态，不能按 envelope 解析或公开 canonical payload。
- Source manifest 的 `type` 是必需的原始 metadata string，保留 homework/custom 等非 mistake 值；只有 mistake 类型具备入库资格，不把该字段伪造为只允许 mistake 的封闭 enum。
- 应用 message 错误与 intake `errorKind/conflictingItemIds` 不统一；framework binding 400、constraint 404、method 405 与 default server/transport 错误可能是 empty、ProblemDetails 或非 JSON。客户端不可据状态码或 DTO 初值猜测成功。
- presigned URL 是短期 bearer 能力，不记录活 URL；实际 auth、Ordinal 前缀、full-only、size/expiry 与存在性语义按该 operation 的条件执行。

现共享 MistakeHttpClient 的 14 个方法在反序列化前按固定版本校验实际 response。添加未知字段可忽略；缺少必需字段、null/type 不匹配、未知已声明 enum 或相矛盾的 pending 结果抛出 JsonException，调用 BFF 现有异常路径保留失败；取消继续传播。普通列表的旧 HTTP failure-to-empty 映射没有在此改写。ReviewBFF 保持 pending false/固定提示与 Completed true，并通过真实 HTTP client 解析；镜像 metadata 保留 operation/returnState/receipt/errorKind。

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
```

`pre-commit.sh` 接入 vocabulary/diff/独立 CLI mutation 检查；它要求可读的 `origin/master` 基线，首次该版本不存在时明确记录 initial_version。已批准的 additive 变更可给验证脚本设置 `MISTAKE_HTTP_ALLOW_ADDITIVE=true`；任何 breaking 仍拒绝。runtime EndpointDataSource 与 spec path/method 集合 exact 对比发现新增/删除业务路由，其他公开 namespace 必须登记主责。发布 bytes/hash、匿名/conditional GET、无业务注册的取消、实际 provider binding/serialization 及 14 concrete consumer 方法分别测试；独立 mutation 对 route、required、type、enum、security、error 缩减必须以 exit 1 失败。

本地 InMemory/OSS doubles 的 wire 测试与真实 PG/S3/正常 CA/提供方 token 验收分开记录。协议业务演练同时覆盖当前来源同 key 重放/异 payload、lease/fence、封口、新 revision、受管退回与交接响应未知及历史恢复、reanalysis Pending/Running；沿现 [SourceIntake](../modules/SourceIntake.md)、[SourceReturns](../modules/SourceReturns.md)、[SourceHandoffs](../modules/SourceHandoffs.md) 验证入口。总数、schema 语法或 spec 可获取都不能单独证明业务验收通过。
