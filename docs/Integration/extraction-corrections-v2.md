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

成功返回 `200`，`data` 同单题 PUT 回执：`{id,studentId,version,question}`。题目 ID、错题 ID 不变，仍为 `PendingReview`；版本号加一。被替换且不再有引用的裁图进入回收队列；其他题、原图和共享图片不动。同键同载荷重放返回原回执，不重新推理、不重复写入，也不会冲掉之后的确认；同键不同载荷 `409 request_payload_conflict`。

推理前、发布时（与确认审核同一把任务锁、同一事务）都检查：目标题仍是 incorrect，绑定的错题仍属于该学生、该任务、该题和学科，且仍为 `PendingReview`。确认审核不推进版本号，因此 AI 运行期间被确认、删除或换绑时返回 `409`，不写入、版本不变。

先校验 AI 的完整结果，任何一条不满足就整体拒绝，什么都不发布：

| 状态 | `errorCode` | 情况 |
|---|---|---|
| 422 | `adjustment_out_of_scope` | 结果不是恰好一道题、ID 不是所选题（涉及其他题、拆题、合并或新增/删除记录）；改了学科；判定不再是 incorrect；内容与另一题完全相同（合并） |
| 422 | `clarification_required` | 新裁剪区域与另一题某个区域的重叠超过该区域面积的 20%，且超过调整前已有的重叠，视为裁进了别的题，请用户说明 |
| 409 | `version_conflict` / `review_conflict` / `item_binding_conflict` | 版本过期；目标不是待审核错题；`itemId` 不匹配或错题已删除、换绑 |
| 404 | `not_found` | 任务不属于调用方，或题目不在当前版本 |
| 502 | `invalid_analysis_response` | 模型输出无效 |

## 原题补图

`POST .../{id}/questions/{questionId}/supplements`：`{requestKey,expectedVersion,itemId,note}`。只针对当前有效错题，将原条目置为 `PendingReview`，显示可读待补说明，发布新的内容版本。不会使用 `Rejected` 或创建新条目。

`POST .../{id}/questions/{questionId}/supplements/{supplementId}/images` 使用 multipart：`requestKey`、`expectedVersion`、`itemId` 和重复 `images`/`images[]` 文件。每次最多 30 张，JPEG/PNG、每张 15 MiB，遵循初次上传的像素与总字节限制。绑定原任务、原题及原错题；原始字节相同则复用，新增原图追加到任务来源。

上传中断后，用原请求键及完全相同的顺序/字节重发，恢复未完成对象，不新建题。接受后保留同一错题、清除待补提示并继续待审核；用 AI 建议或单题 PUT 更新分组。`GET .../supplements/{supplementId}` 可读补图请求/接受回执。不同操作者的准入由 BFF 校验真实学生/学科权限，不能因为客户端持有 ID 就放行。

## 运维迁移

schema v20 不可逆：清空旧版本几何、成功模型/版面检查点及日志旧框；保留原图、当前错题、练习及必要审核元数据。裁图写入前登记，未发布和退役对象经持久队列回收，失败退避后重试。S3 内容版本逐一删除；零字节删除标记不等于未回收图片内容。当前共享引用及独立练习有对象级并发保护。

`Emenda:Extraction:CleanupLegacyObjects=false` 默认不删除存量对象，但**不阻止数据库几何迁移**。测试/生产部署前须审查目标数据清单并取得相应授权；不能只打开开关当作验收。盘点和边界见 [存量裁图盘点](../operations/extraction-crop-inventory.md)。
