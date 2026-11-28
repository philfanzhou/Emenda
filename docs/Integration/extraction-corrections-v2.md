# 纠偏、建议与补图 v2

机器合同：`GET /contracts/emenda-extraction-v2.json`。v1 的发布字节不变，响应带 v2 后继链接。以下为受信 BFF 的服务间接口；服务密钥留在后端。BFF 先验证学生及学科权限，过滤返回内容。管理员对应 `/admin/api/extractions`，写请求须有管理员会话及 CSRF。

## 版本与裁图

保留原图、已接受补图和题目/错题稳定 ID。只有当前 `incorrect` 且未删除的条目有正式裁图；正确、未知、撤回、删除项没有正式裁图。当前题目的各部分按阅读顺序返回。

正式发布使旧几何退役：旧版本/裁图 GET 返回 `410 version_retired`。同键重放仍返回当次接受的版本号，但已过期时 `superseded=true`，题目为空/null；再读取当前任务。版本列表只留操作者、原因、模型、请求键和时间。禁止恢复旧版本几何。

局部改框使用 [单题 PUT](extraction-question-revision.md)；整组 PUT 仅由拥有全集的 BFF 合成。整卷题数没有固定上限；保留图片数、尺寸、每题区域与字节预算。

## 自然语言建议

`POST /api/intake/extractions/{id}/proposals`：

```json
{
  "requestKey": "11111111-1111-4111-8111-111111111111",
  "expectedVersion": 4,
  "instruction": "本题的解答延续到第二张原图上方，请补全；其他题不动。",
  "scope": "questions",
  "questionIds": ["22222222-2222-4222-8222-222222222222"],
  "sourceIds": [],
  "regions": [],
  "previousProposalId": null
}
```

UUID 和版本必须换成当前读出的真实值，不可用示例写业务数据。`questions` 只替换指定题；`regions` 在指定原图查漏题，可用于零错题任务；`all` 才允许整卷重新分组，必须由用户明确选择。空 `sourceIds` 表示可用原图，最多 30 张；超过时先明确选择相关页面。多页在同一次模型请求中处理，并包含当前分组及稳定题 ID。模型不分配新 ID，服务端为新候选分配 ID。

生成只创建持久候选，不写正式裁图、不创建错题、不修改审核。`GET .../{id}/proposals` 列出未处理建议，`GET .../{id}/proposals/{proposalId}` 读候选或终态回执。`Generating` 进行中重发原请求不会重复推理；租约过期后同键可有限重试。新一轮使用新请求键和 `previousProposalId`，继承同范围的未处理候选。不同题目的候选可并存；正式版本变化后都失效。

`POST .../{id}/proposals/{proposalId}/adopt`：`{requestKey,expectedVersion,questions?}`。可选 `questions` 用于微调候选框与内容；局部采纳由服务端合成全集，保持其他题和审核不变。返回接受版本回执；只有这一步生成正式裁图及错题，仍不代表教师确认。`POST .../discard` 丢弃并清空候选几何；重复丢弃幂等。过期、外部任务/题/原图、越范围 ID 和同键变载荷被拒绝。

## 单题调整（直接生效）

`POST .../{id}/questions/{questionId}/adjust`：`{requestKey,expectedVersion,itemId,instruction}`。对应门户「调整」：针对一道**待审核**错题，用一句话说明怎么改；AI 读取该任务全部原图，结果校验通过后直接发布，不经过建议/采纳。`instruction` 去除首尾空白后 1–2000 字。BFF 先校验学生及该题学科权限；调整不能改变学科，因此不会越出已授权范围。

成功返回 `200`，`data` 同单题 PUT 回执：`{id,studentId,version,question}`。题目 ID、错题 ID、题号和年级不变（题号、年级用单题 PUT 修改），仍为 `PendingReview`；版本号加一。被替换且不再有引用的裁图进入回收队列；其他题、原图和共享图片不动。同键同载荷重放返回原回执，不重新推理、不重复写入，也不会冲掉之后的确认；同键不同载荷 `409 request_payload_conflict`。

推理前、发布时（与确认审核同一把任务锁、同一事务）都检查：目标题仍是 incorrect，绑定的错题仍属于该学生、该任务、该题和学科，且仍为 `PendingReview`。确认审核不推进版本号，因此 AI 运行期间被确认、删除或换绑时返回 `409`，不发布调整；为此生成的新裁图立即回收。原图超过 30 张（补图累积）时，先发送该题所在原图，其余按顺序补足 30 张。

先校验 AI 的完整结果，任何一条不满足就整体拒绝，什么都不发布：

| 状态 | `errorCode` | 情况 |
|---|---|---|
| 422 | `adjustment_out_of_scope` | 结果不是恰好一道题、ID 不是所选题（涉及其他题、拆题、合并或新增/删除记录）；改了学科；判定不再是 incorrect；内容与另一题完全相同（合并） |
| 422 | `clarification_required` | 调整后全部区域合计覆盖另一题某个区域的面积，比调整前多出该区域面积的 20% 以上（多个小区域合并计算；原有重叠不计入），视为裁进了别的题，请用户说明 |
| 409 | `version_conflict` / `review_conflict` / `item_binding_conflict` | 版本过期；目标不是待审核错题；`itemId` 不匹配或错题已删除、换绑 |
| 404 | `not_found` | 任务不属于调用方，或题目不在当前版本 |
| 502 | `invalid_analysis_response` | 模型输出无效 |

## 删除单道错题（不是错题）

`POST .../{id}/questions/{questionId}/remove-mistake`：`{expectedVersion,itemId,expectedReviewStatus:1}`。对应门户「不是错题」：把识别错的一道**待审核**错题从本次上传中去掉。BFF 先校验学生及该题学科权限。整份上传的删除见运维删除接口；改判定用单题 PUT。

在同一把任务锁、同一事务内一次检查：任务属于调用方、版本等于 `expectedVersion`、题目仍是 incorrect、`itemId` 仍绑定该学生/任务/题目/学科且仍为 `PendingReview`。确认审核也用这把锁：先确认的，删除返回 `409 review_conflict`，已确认的错题不受影响；先删除的，之后的确认返回 `404 question_not_found`。

删除后：该题从当前版本中消失，绑定的待审核错题被删除，版本号加一。该题的裁图（含各尺寸派生图）以及只为该题上传的补图原图、补图请求一并回收；上传原图、其他题仍在使用的页面、其他题和已确认错题不动。服务端不记录「已排除」名单，之后的重新分析仍可再次识别出这道题。

清理完成且更新后的任务可读时才返回 `200`：`data` 为 `{jobId,studentId,questionId,itemId,version,cleanupComplete:true}`，`version` 为检查时版本加一；BFF 校验回执后重新读取同一任务。请求不带请求键，由 `expectedVersion` 与 `itemId` 标识这次删除：响应丢失或清理未完成时，原样重发即重放同一回执并继续清理，不会删除更新的或无关的数据。

| 状态 | `errorCode` | 情况 |
|---|---|---|
| 400 | `invalid_request` | 缺字段、`expectedReviewStatus` 不是 1 |
| 404 | `not_found` | 任务不属于调用方，或题目不在当前版本（包括已被删除后再用新版本号请求） |
| 409 | `version_conflict` / `review_conflict` / `item_binding_conflict` | 版本过期；已确认或不是待审核错题；`itemId` 不匹配或已换绑 |
| 503 | `cleanup_pending` | 删除已提交，但对象存储清理未完成（失败后退避重试）；原样重发直到返回 200 |

## 补充分析提示（整份重新分析）

`POST .../{id}/reanalyze`：`{requestKey,expectedVersion,instruction,allowedSubjects}`。对应门户「补充分析提示」：用户用一句话补充说明，例如「第二页是第一页的续答」「第 15 题也是错题」「刚才误点了，第 15 题实际是错题」。AI 读取该任务全部原图和当前识别结果，结果校验通过后直接发布到同一任务，不经过建议/采纳。`instruction` 去除首尾空白后 1–2000 字。`allowedSubjects` 是 BFF 校验后该操作者可以修改的学科（1–9，至少一个，顺序和重复不影响）。任务可以没有错题。

只改提示涉及的内容：

- AI 返回带原 ID 的题，视为修改这道题，题目 ID 和错题 ID 不变。之前判为正确或未知的题被改判为错题时，就在原题上创建待审核错题，不另建新题。
- AI 返回不带 ID 的题，视为新题：服务端分配新 ID，按它在原图上的起始位置插入阅读顺序；判为 incorrect 时创建一道待审核错题。
- AI 没有返回的题保持原样，不会因此被撤回。
- 已审核（确认或驳回）的题、错题已被删除的题、不在 `allowedSubjects` 内的题只作为上下文发给 AI；AI 对它们的改动一律忽略。
- 用「不是错题」删除的题不在当前结果里，重新分析可以再次识别出来。它是一道新题，题目 ID 和错题 ID 都是新的，裁图从保留的原图重新生成。服务端不保存旧裁图、旧结果或「已排除」名单。

没有任何变化时返回 `200`，版本号不变；有变化时版本号加一。`data` 为 `{jobId,studentId,requestKey,version}`；BFF 校验回执后重新读取同一任务。同键同载荷重放返回原回执（包括无变化的回执），不重新推理，也不会重复创建错题或裁图；同键不同载荷 `409 request_payload_conflict`。

推理在任务锁之外进行。发布时重新检查版本号，并与确认审核在同一把任务锁、同一事务内检查：每道将被修改的待审核错题仍属于该学生、该任务、该题，且仍为 `PendingReview`。AI 运行期间，要修改的错题被确认时返回 `409 review_conflict`，已确认的错题不受影响；期间有删除或其他修改时返回 `409 version_conflict`。为此生成的新裁图立即回收。原图超过 30 张（补图累积）时返回 `413 analysis_request_too_large`。

先校验 AI 的完整结果，任何一条不满足就整体拒绝，当前结果保持不变：

| 状态 | `errorCode` | 情况 |
|---|---|---|
| 400 | `invalid_request` | 缺字段、提示为空或超过 2000 字、`allowedSubjects` 为空或含 1–9 以外的值 |
| 422 | `reanalysis_out_of_scope` | 待审核错题被改成不是 incorrect（撤回错题请用「不是错题」）；修改后或新增的题不在 `allowedSubjects` 内 |
| 422 | `clarification_required` | 新题覆盖某道现有题某个区域超过该区域面积的 20%（可能是同一道题被重复识别）；修改后的题新覆盖另一题区域超过 20%（规则同单题调整）；两道题内容完全相同 |
| 409 | `version_conflict` / `review_conflict` / `request_payload_conflict` / `processing_in_progress` | 版本过期或推理期间任务变化；任务正在识别中；要修改的错题已被确认；同键不同载荷 |
| 404 | `not_found` | 任务不属于调用方 |
| 413 | `analysis_request_too_large` | 原图超过 30 张 |
| 502 | `invalid_analysis_response` | 模型输出无效（未知或重复的题目 ID、无效区域等） |
| 503 | `extraction_disabled` | 识别功能未开启 |

「做对了也收录」（`includedByRequest`）还不支持。

## 原题补图

`POST .../{id}/questions/{questionId}/supplements`：`{requestKey,expectedVersion,itemId,note}`。只针对当前有效错题，将原条目置为 `PendingReview`，显示可读待补说明，发布新的内容版本。不会使用 `Rejected` 或创建新条目。

`POST .../{id}/questions/{questionId}/supplements/{supplementId}/images` 使用 multipart：`requestKey`、`expectedVersion`、`itemId` 和重复 `images`/`images[]` 文件。每次最多 30 张，JPEG/PNG、每张 15 MiB，遵循初次上传的像素与总字节限制。绑定原任务、原题及原错题；原始字节相同则复用，新增原图追加到任务来源。

上传中断后，用原请求键及完全相同的顺序/字节重发，恢复未完成对象，不新建题。接受后保留同一错题、清除待补提示并继续待审核；用 AI 建议或单题 PUT 更新分组。`GET .../supplements/{supplementId}` 可读补图请求/接受回执。不同操作者的准入由 BFF 校验真实学生/学科权限，不能因为客户端持有 ID 就放行。

## 运维迁移

schema v20 不可逆：清空旧版本几何、成功模型/版面检查点及日志旧框；保留原图、当前错题、练习及必要审核元数据。裁图写入前登记，未发布和退役对象经持久队列回收，失败退避后重试。S3 内容版本逐一删除；零字节删除标记不等于未回收图片内容。当前共享引用及独立练习有对象级并发保护。

`Emenda:Extraction:CleanupLegacyObjects=false` 默认不删除存量对象，但**不阻止数据库几何迁移**。测试/生产部署前须审查目标数据清单并取得相应授权；不能只打开开关当作验收。盘点和边界见 [存量裁图盘点](../operations/extraction-crop-inventory.md)。
