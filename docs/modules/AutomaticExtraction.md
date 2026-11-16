# 自动提取与判错

该能力对应需求确认稿 V2：独立上传一张或多张照片，经 StructaDoc 版面分析、AI Tutor 多图组题与判错后，只有具有学生实际作答及错误证据的题目创建待审核错题。结果为 `correct`、`incorrect` 或 `unknown`。模型答案是参考解答，不是已确认的错因；不自动填写 `RootCause`。

## 默认规则

- 以完整题目组为粒度，支持一图多题、多图一题和公共题干引用。
- 每批最多 9 张 JPEG/PNG，每张 15 MiB、单维 8192 像素、总像素 1600 万、单帧。重复字节照片保留第一次的来源，不重复传输推理。
- 缺实际作答、读不清、材料不足或无法判断时，不自动创建错题。
- 原图不修改；所有框使用 EXIF 转正后的显示帧 `[0,1]` 坐标。裁剪保留完整像素，不通过生成式模型重绘。
- 对错、分组、区域和来源快照按版本保留，不做自动清理。错题删除后保留题目映射墓碑，不通过重放或历史恢复重新创建它。
- 管理员纠偏修改的是当前内容版本；已有练习不删除，旧审核/错因/图片写入过程日志。内容变化后的错误题重新待审核，改判正确、无法判断或撤回的既有错题标为退回。

## 启用

现有 `/api/intake/uploads` 同步合同不变。新能力默认关闭，独立版通过以下配置启用：

| 配置 | 要求 |
|---|---|
| `Emenda__Extraction__Enabled` | `true` |
| `Emenda__Extraction__TutorBaseUrl` | 运维配置的 AI Tutor HTTP(S) origin，无路径、凭据、query 或 fragment |
| `Emenda__Extraction__TutorApiKey` | 独立服务密钥，32–256 字符，与 AI Tutor 配置一致 |
| `Emenda__Extraction__OperationTimeoutSeconds` | AI 分析预算，默认 300 秒，范围 1–1800 |
| `Mistake__Layout__Enabled` | `true`，沿用既有 StructaDoc BaseUrl/ApiKey 配置 |

AI Tutor 配套实现新增 `POST /api/v1/problems/analyze-images`。需配置 `EMENDA_ANALYSIS_KEY` 和 `QUESTION_INTAKE_MODEL`，后者必须是已有启用模型配置，不硬编码供应商或模型名称。运行在来源受限后端网络，浏览器不得持有服务密钥。部署顺序为先升级 AI Tutor 并验证能力，再启用 Emenda；不对旧供应商回退，不把聊天自由文本解析为判错合同。

AI Tutor 配套源码通过 [基线与完整补丁](../../integrations/ai-tutor/README.md) 持久交付，已经在任务自有 checkout 实现并检查；Emenda 镜像不会包含或自动部署它。

## 接口

服务调用方使用原有 `X-Emenda-Api-Key`；来源键按调用方隔离，学生授权仍由受信任调用方负责。管理员使用既有 SignaCore 会话和 CSRF，不在浏览器输入服务密钥。

| 方法 | `/api/intake/extractions` 下路径 | 语义 |
|---|---|---|
| POST | 空路径 | multipart `studentId`、`grade`、必填 `sourceKey`、重复 `images` 或 `images[]`；返回 202 和任务 |
| GET | 空路径 | 可选 `studentId`、`page`；每页 20 条 |
| GET | `/{id}` | 来源、状态、当前结果和版本列表 |
| POST | `/{id}/retry` | `{requestKey}`；仅失败且未完成任务，最多总计 5 次尝试 |
| GET | `/{id}/sources/{sourceId}` | 校验摘要后读取精确原图 |
| GET | `/{id}/versions/{version}` | 只读历史快照 |
| GET | `/{id}/questions/{questionId}/images/{version}/{index}` | 零基 index 的历史 PNG 裁图 |

管理端 `/admin/extractions` 提供上传、查询、三态结果、原图框选、数值坐标、多图区域重排、整组修订和历史读取。`/admin/api/extractions` 提供同一读取/上传/重试操作，以及仅管理员可用的：

`PUT /{id}/questions`

请求为 `{requestKey,expectedVersion,reason,questions}`，提交完整分组集合。每题保留稳定 UUID、学科、题干、实际作答、参考答案、说明、结论、证据和区域集合。新题由客户端生成 UUID；省略既有题表示撤回，不物理删除历史。正确/错误必须提供实际作答、参考答案和证据；区域必须属于该任务原图。服务端再校验，不能绕过前端确认。

同键同内容上传不会重复建题；同键不同学生、年级或有序图片内容返回 409 `source_conflict`。修订同 requestKey 同载荷重放接受版本，不同载荷 409 `request_payload_conflict`；版本过期 409 `version_conflict`，页面保留草稿。失败重试 UUID 持久保留，重复该 UUID 不增加任务。

响应统一 `{success:true,data:...}`，错误 `{success:false,errorCode:...}`。处理和恢复状态可见，不能把上传成功或 HTTP 202 当作业务入库完成。

## 持久化与恢复

迁移 `20261116000000_AddExtractions` 为 schema v16，新增 `extraction_job`、`extraction_question`、`extraction_revision`。独立 Own 模式按现有严格升级器升级；Shared 模式不执行 DDL，未具备迁移时拒绝启动。非空 Down 拒绝，备份包含三表及原图和历史裁图。

先持久绑定上传载荷，再写入并回读校验原图；存储失败可以用原 sourceKey 和原字节重新提交。后台以数据库时钟、租约、心跳、owner 和 fence 领取任务；进程中断后过期任务可重新领取，迟到 worker 不能发布。已接受布局与模型结果保存为检查点，裁剪或入库失败后重试不重新调用已经接受的模型。上游响应丢失仍可能重复推理，不承诺跨服务 exactly-once。

最终错题、过程日志、题目映射、版本和任务状态在一个 PostgreSQL 事务提交。对象存储写入在事务外执行，键绑定任务、修订及内容摘要；未完成对象保留用于恢复，不自动删除。人工修订使用 expectedVersion 和短事务发布。

## 验证边界

使用任务自有 PostgreSQL 15 数据库验证 schema 升级、不可变历史、并发领取、载荷重放、裁剪失败恢复、人工修订、旧图读取和删除墓碑。使用受控模型验证两端 JSON 合同，不代表真实模型质量验收。实际上线还需按学科和题型人工标注，测量误收、漏收、无法判断比例、组题完整率、裁剪质量、耗时和成本。

## 本次本地验证记录

- Release 构建通过，0 警告、0 错误。
- 新能力 40 项定向测试通过：PostgreSQL 15.19 的迁移与不可变历史、异步任务与 fencing、并发重放、裁剪失败检查点恢复、纠偏与历史图片、删除墓碑、真实 Host 认证及 CSRF、客户端 wire 校验、EXIF 和整批图片预算。
- 配套 AI Tutor 补丁在干净的精确基线上 `git apply --check` 成功，应用后类型检查、构建和 8 项分析接口测试通过，包含分钟限流回归。
- 浏览器使用真实 Kestrel Host、PostgreSQL 15 和管理员服务端会话，走通合成图片上传、异步完成、数值区域编辑、保存 v2 和读取 v1 历史。历史原图与裁图均成功解码；375px 布局未发现横向溢出。外部 AI 和对象存储使用受控适配器，不代表真实服务商验收。截图工具超时，未完成截图视觉复核。
- 相关既有回归中，同步上传、管理员控制台、旧审核、模型快照等 51 项通过；旧启动测试在专用 PG15 上 45 项通过。凭据拒绝用例改用实际 SCRAM 密码认证后通过。`LocalHttpPortUsesEnvironmentBeforeFileAndRestartAppliesNewValueAsync` 的非目标端口应拒绝连接断言失败，未修改与本功能无关的端口逻辑，不能将该启动套件报告为全部通过。
- 未调用真实模型处理学生图片；未部署、未执行业务数据库迁移、未做真实判错准确率验收。本地测试使用临时数据库，配套服务需分别发布后才能在实际环境启用。
