# 自动提取与判错

该能力对应需求确认稿 V2：独立上传一张或多张照片，经 StructaDoc 版面分析、AI Tutor 多图组题与判错后，只有具有学生实际作答及错误证据的题目创建待审核错题。结果为 `correct`、`incorrect` 或 `unknown`。模型答案是参考解答，不是已确认的错因；不自动填写 `RootCause`。

## 默认规则

- 以完整题目组为粒度，支持一图多题、多图一题和公共题干引用。
- 每批按卷面顺序上传，最多 30 张 JPEG/PNG，每张 15 MiB、单维 8192 像素、总像素 1600 万、单帧。重复字节照片保留第一次的来源，不重复传输推理。
- 缺实际作答、读不清、材料不足或无法判断时，不自动创建错题。
- 原图不修改；所有框使用 EXIF 转正后的显示帧 `[0,1]` 坐标。裁剪保留完整像素，不通过生成式模型重绘。
- 对错、分组、区域和来源快照按版本保留，不做自动清理。错题删除后保留题目映射墓碑，不通过重放或历史恢复重新创建它。
- 纠偏修改的是当前内容版本；已有练习不删除，旧审核/错因/图片写入过程日志。内容变化后的错误题重新待审核，改判正确、无法判断或撤回的既有错题标为退回。

### 跨页题目与编号

同一题可同时引用任意试卷页面中的多个区域。模型依据题干、图形和作答的连续性组题，不按相同题号强行合并；无法确认的内容保留为 `unknown`，由管理员合并或拆分。

- `questionNumber` 保存原卷面题号，例如 `12`、`三、2（1）`；缺失或无法辨认时为 `null` / 省略，不用列表位置补号。题号允许重复，不承担唯一身份。非空值最多 64 字符，不允许首尾空白或控制字符；管理界面会去除首尾空白并将空白转为未知。
- `question.id` 是稳定 UUID；修正题号或调整片段顺序不会改变它，也不会新建同一条错题。题号是提取结果及版本快照的元数据，通过已有 `itemId` / `questionId` 关联错题，不重复新增一套数据库编号列。
- `question.regions` 数组是阅读顺序的唯一来源，支持先背面再正面，不被照片上传顺序覆盖。`images` 按相同顺序保存裁图。页面显示“第 1、2 部分”，图片 API 的 `index` 和对象 key 仍从 0 开始；原图、区域坐标和内容摘要均保留。
- 管理界面可修改题号、调整部分顺序、将当前组合并到指定 UUID，或勾选部分拆为新 UUID。合并保留目标 UUID 和题号，区域按目标在前追加；拆分保留原组 UUID，新组题号留空。合并或拆分后判定重置为 `unknown`，保留文字供人工重新核对；仅显式保存完整纠偏才发布新版本。
- 仅修正题号不会撤销既有审核或人工错因，区域不变时复用原裁图。改变题目内容、片段顺序或分组仍按纠偏规则重新待审核。历史版本保持当时题号、分组、片段顺序和可读取图片。

该字段为兼容性扩展，无新增数据库迁移。旧快照及已接受模型检查点缺失题号时按未知读取；未知题号序列化时省略，保留旧版无题号修订的载荷哈希与幂等身份。

## 启用

这是唯一的图片入库入口（旧的 `/api/intake/uploads` 同步入口已移除）。能力默认关闭，通过以下配置启用：

| 配置 | 要求 |
|---|---|
| `Emenda__Extraction__Enabled` | `true` |
| `Emenda__Extraction__TutorBaseUrl` | 运维配置的 AI Tutor HTTP(S) origin，无路径、凭据、query 或 fragment |
| `Emenda__Extraction__TutorApiKey` | 独立服务密钥，32–256 字符，与 AI Tutor 配置一致 |
| `Emenda__Extraction__OperationTimeoutSeconds` | AI 分析预算，默认 300 秒，范围 1–1800 |
| `Mistake__Layout__Enabled` | `true`，沿用既有 StructaDoc BaseUrl/ApiKey 配置 |

AI Tutor 配套实现新增 `POST /api/v1/problems/analyze-images`。需配置 `EMENDA_ANALYSIS_KEY` 和 `QUESTION_INTAKE_MODEL`，后者必须是已有启用模型配置，不硬编码供应商或模型名称。运行在来源受限后端网络，浏览器不得持有服务密钥。部署顺序为先升级 AI Tutor 并验证能力，再启用 Emenda；不对旧供应商回退，不把聊天自由文本解析为判错合同。

AI Tutor 配套源码通过 [基线与完整补丁](../../integrations/ai-tutor/README.md) 持久交付，已经在任务自有 checkout 实现并检查；Emenda 镜像不会包含或自动部署它。

### Portal 的 30 页接入条件

Emenda 已发布的 `v0.1.1-rc.5` 仍限 9 页。首个兼容版本应同时包含本次 Emenda 上传改动，并搭配接受 30 张图的 AI Tutor。发布后以实际 Git 标签和镜像摘要锁定版本。

- 30 张满额原图共 450 MiB，Emenda 再允许 64 KiB 表单开销。上传路径每层反向代理应允许至少 451 MiB。ASP.NET Core 会把大表单分片暂存到磁盘，服务受理时还会持有全部原图字节；按并发上传数预留临时空间和内存。
- 上传完成即返回任务，Portal 轮询结果。版面检测逐页执行并保存进度；AI 分析的默认 300 秒从整卷分析调用开始计算。部署时按慢速上传、逐页版面检测和模型响应分别验证超时。
- 同一次分析携带全部有序预览，JSON 请求最多 8 MiB。页数越多，每页预览可用字节越少；原图和审核裁图保留原始分辨率。响应上限 2 MiB，每卷最多 32 个完整题组，每组最多 32 个区域。上线前用目标模型核验密集小字和跨页题。

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
| PUT | `/{id}/questions` | 保存本调用方任务的完整题目分组；请求与管理端相同，其他调用方的任务返回 404 |

学生或管理该学生的老师、助教重裁时，由业务后端根据任务实际关联的 `studentId` 校验当前用户权限，服务密钥不发给浏览器。只改一处裁图也须提交全部题组，保留题目 UUID 和其他区域，只修改目标区域坐标。同一错题保留原 `itemId`，保存裁切不等于确认；确认或退回应使用返回的新版本调用审核接口。

PC 管理页「错题入库」（`/admin/extractions`）展示客户端提交的任务，可按学生筛选并对照原图查看判题结果、整组纠偏和历史版本。图片通过客户端 `POST /api/intake/extractions` 提交。管理接口 `/admin/api/extractions` 支持读取、失败重试及版本绑定审核，并提供整组修订：

`PUT /{id}/questions`

请求为 `{requestKey,expectedVersion,reason,questions}`，提交完整分组集合。每题保留稳定 UUID、学科、题干、实际作答、参考答案、说明、结论、证据和区域集合。新题由客户端生成 UUID；省略既有题表示撤回，不物理删除历史。正确/错误必须提供实际作答、参考答案和证据；区域必须属于该任务原图。服务端再校验，不能绕过前端确认。

同键同内容上传不会重复建题；同键不同学生、年级或有序图片内容返回 409 `source_conflict`。修订同 requestKey 同载荷重放接受版本，不同载荷 409 `request_payload_conflict`；版本过期 409 `version_conflict`，页面保留草稿。失败重试 UUID 持久保留，重复该 UUID 不增加任务。

响应统一 `{success:true,data:...}`，错误 `{success:false,errorCode:...}`。处理和恢复状态可见，不能把上传成功或 HTTP 202 当作业务入库完成。

## 持久化与恢复

迁移 `20261116000000_AddExtractions` 为 schema v16，新增 `extraction_job`、`extraction_question`、`extraction_revision`。启动时由严格升级器升级。非空 Down 拒绝，备份包含三表及原图和历史裁图。

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

### 2026-11-17 跨页题号与分片顺序验收

- 本地 PostgreSQL 15.19 独占实例：原提取专项及新增题号测试共 50 项通过，另补充旧版无题号修订回执兼容测试 1 项通过；均未跳过。覆盖跨图组题、重复题号不强行合并、题号边界、改号保留审核、重排后的精确裁图字节、人工合并/拆分、稳定 UUID 和历史读取。
- 配套 AI Tutor 完整补丁在精确基线 `27c615a44049f668d0154d4f3b86eef283a061a2` 上应用检查、类型检查、构建及 10 项分析接口测试通过。模型使用受控测试适配器，不代表真实照片识别准确率。
- 浏览器使用真实 Kestrel、独占 PostgreSQL 和合成管理员会话：两张合成原图归属一个题目，完成题号去空白保存、正反面片段重排、拆分再合并、历史 v1 题号和两幅裁图读取。UUID 不变；合并/拆分判定重置为未知。HTML 形式题号按文字显示，清空题号显示未知；375px 布局 `scrollWidth=375`，截图已检查，未发现横向溢出。
- 浏览器的模型、版面分析和文件存储使用现有受控测试适配器；未调用真实 AI Tutor / StructaDoc / S3，也未走真实 SignaCore 登录。本次未升级服务器上已部署的 `emenda-biztest`，未提交或推送代码。

### Pre-merge isolation and version-bound review

Extraction upload ownership uses disjoint persisted namespaces: administrator uploads and named service callers cannot collide, even when a configured caller is named `admin` or includes a namespace delimiter. Source-key replay is scoped to this typed owner. This is the initial, unreleased extraction representation; no ambiguous legacy-owner alias is accepted.

Originals and retained crops are read only through the authenticated extraction byte endpoints. The legacy `/api/mistakes/presigned-url` endpoint refuses extraction paths and normalized aliases, including derived thumbnails, before requesting an OSS signature. Existing non-extraction image signing remains unchanged.

`POST /api/intake/extractions/{id}/questions/{questionId}/review` accepts `expectedVersion`, `itemId`, `reviewStatus`, `reviewerId`, `rootCause` and `returnReason`. The trusted BFF supplies the authenticated reviewer (maximum 36 characters, matching the existing schema) and inspected positive version; administrators use their session actor. A shared job advisory lock and the domain database transaction bind the version check, question/item membership and review mutation to one revision. Stale versions return `409 version_conflict`; only pending items may transition once to confirmed or rejected. Rejection reasons are retained in that transaction's review log. The route is also available under the existing admin session/CSRF boundary and returns the complete existing review response DTO. The legacy lifecycle review route refuses extraction-generated items, requiring the conditional route; other items keep their existing contract.

Multipart parsing permits three required text fields plus 30 ordered image parts. A 31st image is rejected before a job is created.

Pre-merge validation: all 60 focused extraction/client/HTTP tests passed against an owned disposable PostgreSQL 15 instance, with no skips or compiler warnings. Coverage includes arbitrary caller-name collisions, foreign/admin originals and crops, denied legacy signatures and aliases, valid eight/nine-image upload admission, rejected ten-image requests, stale-version review and legacy-route bypass refusal, terminal-state conflicts, reviewer schema limits, persisted rejection reasons and the complete response consumed by the Portal client. The instance was stopped and its data removed. No production database or service was changed.
