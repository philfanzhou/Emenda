# StructaDoc 版面检测集成

Mistake 通过专用非归档端点 `POST /api/v1/layout/detect` 调用外部仓库 [StructaDoc](https://github.com/philfanzhou/StructaDoc)（v0.1.13，`6ec800ca729307fd3ac1ccfa8776e6877aef3140`）做学习照片的版面检测，为自动拆题保留区域提案（[ADR-0023](../../../../../docs/adr/0023-mistake-structadoc-layout-detection.md)）。一次调用即一次同步 HTTP 交换：无提交、无轮询、无可再取回的任务资源；StructaDoc 对该路由不归档任何内容。区域是版面提案，不是题目，也不是学生答错的证据。本文是消费侧契约权威；上游行为以其仓库文档为准。

## 能力开关与配置

- `Mistake:Layout:Enabled`（环境键 `Mistake__Layout__Enabled`）：启动快照，默认 `false`，只接受 bool。关闭时零 layout 请求：客户端注册到保留占位地址 `http://structadoc-layout-disabled.invalid`，应用接口立即报告版面阶段失败，既有处理完全不变。
- `Mistake:Layout:BaseUrl`：管理员配置的 StructaDoc 服务根地址（绝对 HTTP(S)，不含凭据、路径、查询或片段；客户端追加 `api/v1/layout/detect`）。启用时必填。
- `Mistake:Layout:ApiKey`：scoped 部署密钥（专用客户端 `ruoyu-study-layout`，仅 `layout:detect` scope）。启用时必填；以 `Authorization: ApiKey <credential>` 呈现，只经部署密钥配置解析，日志中仅以 `SensitiveDataMasker.MaskApiKey` 掩码形态出现。不接受调用方提供的 URL 或凭据，不转发终端用户 JWT。
- `Mistake:Layout:PerImageBudgetSeconds`：单图预算，默认 180（1–3600）。云交换自身的 120 秒执行期截止之后仍有排队余量；HTTP 客户端注册为无限超时，绝不继承框架 100 秒默认。
- `Mistake:Layout:MaxConcurrentExchanges`：进程内并发交换上限，默认 4（1–16），与已部署云适配端的四路并发一致。
- `Mistake:Layout:MaxResponseBytes`：单响应体上限，默认 4 MiB（4096–67108864）。

启用时上述校验在任何数据库访问前以键名失败；修改任一值须重启进程。配置与回退步骤见[部署说明](../development/Deployment.md)的「版面检测启用与回退」。

## 请求契约

multipart/form-data，恰好一个名为 `file` 的文件节、无其他表单字段；原图字节原样传输（不自动转向、不缩放、不压缩）。客户端在发送前本地校验并拒发（零请求）：JPEG/PNG 签名（JPEG `FF D8 FF`；PNG 8 字节签名 + IHDR）、声明 MIME 与签名一致、不超过 32 MiB 图像字节、单维不超过 32,000 像素、总像素不超过 100,000,000。尺寸来自文件头（PNG IHDR / 首个 JPEG SOF），不做像素解码。

## 响应契约与几何

200 响应必含 `requestId`、`width`、`height`、`coordinateFrame`、`regions`；`regions` 每项含 `id`、`label`、`x0`、`y0`、`x1`、`y1`；可选 `orientation`（1–8 或 null）、`engine`、`model`、`version`、`diagnostics`。线上无 `schemaVersion` 字段；消费方自行持久化契约标识。

- `width`/`height` 描述提交图像的存储像素帧；`coordinateFrame` 恒为 `top-left-origin-normalized`（左上原点、x 向右、y 向下、坐标归一到 [0,1]）。
- 校验（Domain 纯几何模块 `LayoutGeometry`）：坐标有限且有序（x0≤x1、y0≤y1，左上角在前）、[0,1] 范围、响应内唯一区域 ID、非空 label、正维度、orientation 1–8。非法或不兼容输出是阶段错误，绝不为空成功；显式空 `regions` 是合法答案，不推断"照片中没有题"。
- 区域保持上游检测顺序；该顺序不是保证的阅读/题目顺序。区域 ID 是响应内局部编号，不是稳定题目 ID。
- 几何模块提供归一化框与存储像素坐标互转（连续坐标、精确往返）、EXIF 1–8 旋转/镜像到显示帧的映射及其逆映射（不双重旋转）；快照的独立 `imageRotations` 列表仅按原顺序/长度保留为元数据（Student 的旋转操作已重写像素并保留该列表不变），绝不叠加为额外旋转；旧整数矩形不重新解释为归一化框。

## 失败分类

客户端按 HTTP 状态 + Problem Details `code` 共同分类（`LayoutDetectionException.Failure`），不盲目重试所有 5xx。`IsTransient` 仅对传输故障、过载（尊重 `Retry-After`，delay-seconds 或 HTTP-date）、上游超时、运行时不可用为真：

| 状态 + code | 分类 | 瞬态 |
|---|---|---|
| 本地：能力关闭 | `CapabilityDisabled` | 否 |
| 本地：启用但配置不完整 | `Configuration` | 否 |
| 本地：签名/MIME/字节/像素校验、400 `layout-request-invalid`、413 `layout-input-too-large`、415 `layout-media-type-unsupported` | `InvalidInput` | 否 |
| 401 / 403 | `Authentication` | 否 |
| 429 `layout-overloaded`（含 `Retry-After`） | `Overloaded` | 是 |
| 504 `layout-timeout`、本地单图预算到期 | `UpstreamTimeout` | 是 |
| 503 `layout-capability-unsupported` | `UnsupportedCapability` | 否 |
| 502 / 503 `layout-runtime-unavailable` | `RuntimeUnavailable` | 是 |
| 502 `layout-runtime-rejected` | `RuntimeRejected` | 否 |
| 502 `layout-output-invalid` | `InvalidOutput` | 否 |
| 200 但 JSON/几何/坐标帧/尺寸违约或超限 | `MalformedResponse` | 否 |
| 传输层故障（连接、DNS） | `TransientTransport` | 是 |
| 上述之外的状态 + code 组合 | `UnexpectedResponse` | 否 |

异常消息只含状态、code 与固定措辞；不含凭钥、图像字节或原始上游响应文本。调用方职责：提供已核验的原图字节（字节核验属持久化阶段）、传递取消（取消原样传播，不吞为分类）、把分类失败当运营结果而非重试一切信号。

## 契约固定与验证

消费侧 fixture `src/Tests/Ruoyu.Study.Mistake.Service.Tests/Fixtures/structadoc-layout-v0.1.13-openapi.json` 钉住 v0.1.13 的 `/api/v1/layout/detect` 操作（请求形状、响应模式、错误状态与 code 全集、可选元数据、无 schemaVersion 字段）；实现沙箱不可达部署实例，fixture 由 v0.1.13 tagged 源码重建，可达时应拉取部署的 `/api/v1/openapi.json` 比对刷新。

验证：`LayoutGeometryTests`（几何已知坐标、EXIF 全 8 向、往返、快照旋转元数据、校验规则）、`StructaDocLayoutClientTests`（请求形状、分类表、零请求本地校验、Retry-After、取消）、`StructaDocLayoutContractServerTests`（loopback Kestrel 临时端口的真实 HTTP：multipart 实交换、预算治理、并发上限、流式超限拒绝）、`StructaDocLayoutContractFixtureTests`（fixture 钉住与分类交叉验证）、`LayoutCapabilityStartupTests`（启动快照：默认关闭、非法 bool、启用配置门禁、掩码凭钥、预算与无限客户端超时证据、启动零请求）。在 `src/services/ruoyu.mistake/src` 运行 `dotnet build Ruoyu.Study.Mistake.sln --configuration Release` 与 `dotnet test Ruoyu.Study.Mistake.sln --configuration Release`；无需 PostgreSQL。

## 已部署端点冒烟

以下程序针对**已部署的** StructaDoc 公共 layout 端点（测试基址与 scoped `ruoyu-study-layout` Key，部署密钥配置；基址记录于 tracker IKQ6S3），验证消费路径与 StructaDoc 无归档副作用。它是**活云检查**，与受控合同测试（fixture 钉住 v0.1.13、loopback 假服务器、provider PG wire 套件）性质不同：不进入任何自动化套件，需操作员显式执行；不构成题目拆分准确性主张；上游推理输出不承诺可复现。不部署本地 worker。

前置：能访问部署基址的管理员；部署密钥中的 scoped Key（仅 `layout:detect`）；一张非敏感测试照片（仓库 `tests/integration/fixtures/` 下的现有测试图片或自制 PNG/JPEG，不得含学生个人信息）。

```bash
# 1) 直接调用部署端点：multipart 单文件节 file，验证 200、requestId、坐标帧与区域
#    形状（响应契约见上文），Authorization: ApiKey <layout-scoped-key>
curl --cacert '<CA-file>' -H "Authorization: ApiKey <layout-scoped-key>" \
  -F "file=@<non-sensitive-fixture.png>" \
  https://<structadoc-test-base-url>/api/v1/layout/detect

# 2) 通过 Mistake 受管操作走完整消费路径（Admin subject；基址与凭据在部署配置中）：
#    POST /api/mistakes/intakes/{sourceId}/{revision}/layout  {"requestKey":"<uuid>"}
#    轮询 GET 同路径，直到出现接受结果（validated regions、upstream requestId）
# 3) 在 StructaDoc 管理端核对：无新 Document、无新 Parse Run（该路由不归档）；
#    scoped Key 权限只有 layout:detect，无文档库权限
```

第 2 步要求既有受管来源修订（`Mistake:Layout:Enabled` 开启并配置完成后由受管 intake 产生）；执行后核对响应与持久结果一致。任何一步失败即如实记录，不得改用商业 API 或 Document/Parse Run 兜底。

## 边界

不调用 StructaDoc Document/Parse Run 端点、不需要文档库权限、不静默切换商业 API；所选 MinerU Cloud 后端内部的完整抽取是允许且预期的。作业/结果持久化（迁移 13）与受管 start/status 操作（[LayoutJobs](../modules/LayoutJobs.md)，additive 登记于[版本化机器合同](./MachineHttpContract.md)）已交付；本集成在 Mistake 进程内，不新建服务，不部署本地 layout worker，不持有重复 MinerU 凭据。
