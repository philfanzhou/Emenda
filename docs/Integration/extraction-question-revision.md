# 单题纠偏接入

适用合同：`emenda-extraction-v2.json`，版本 `2.0.0`。单题写入、AI 建议和补图配套 schema v20；服务调用方认证及整组写入仍保留。旧版本几何退役属于明确行为变化，调用方须处理 `410` 与 `superseded` 回执。

## 请求和回读

仅业务后端调用。先按任务实际 `studentId` 和目标题 `subject` 校验当前用户权限，取得用户看过的任务版本、目标题及原图编号。不要向浏览器下发服务密钥或其他学科题目。路径 UUID 和 `question.id` 必须一致。

以下为完整 JSON 示例；UUID、版本、题目内容和坐标须换成已读取的真实目标，不能以示例 UUID 写业务数据。每次新操作生成新的 `requestKey`；响应丢失时原样重发该次请求。

```json
{
  "requestKey": "11111111-1111-4111-8111-111111111111",
  "expectedVersion": 3,
  "reason": "补全第二部分漏掉的作答，其他部分保持原样",
  "question": {
    "id": "22222222-2222-4222-8222-222222222222",
    "questionNumber": "12",
    "subject": 2,
    "statement": "计算 36 + 27。",
    "studentAnswer": "61",
    "referenceAnswer": "63",
    "explanation": "个位 6 + 7 = 13，进一后十位为 6。",
    "outcome": "incorrect",
    "evidence": "原图上学生写了 61，正确结果为 63。",
    "regions": [
      { "sourceId": "33333333-3333-4333-8333-333333333333", "x0": 0.1, "y0": 0.6, "x1": 0.9, "y1": 0.95 },
      { "sourceId": "44444444-4444-4444-8444-444444444444", "x0": 0.1, "y0": 0.05, "x1": 0.9, "y1": 0.3 }
    ]
  }
}
```

将该 JSON 保存为 `correction.json`，配置后端进程的 `EMENDA_BASE_URL`、`EMENDA_API_KEY`、`JOB_ID`、`QUESTION_ID` 后调用：

```bash
curl --fail-with-body --silent --show-error \
  -X PUT "$EMENDA_BASE_URL/api/intake/extractions/$JOB_ID/questions/$QUESTION_ID" \
  -H "X-Emenda-Api-Key: $EMENDA_API_KEY" \
  -H 'Content-Type: application/json' \
  --data-binary @correction.json
```

成功响应为 `{success:true,data:{id,studentId,version,question}}`；`question` 包含 `question`（题目内容）、`itemId`、`itemDeleted` 和有序 `images`。响应不会携带其他题或历史快照。使用接受的 `version`、原 `question.id` 和原 `itemId` 回读裁图及确认审核：

```bash
curl --fail-with-body --silent --show-error \
  "$EMENDA_BASE_URL/api/intake/extractions/$JOB_ID/questions/$QUESTION_ID/images/$ACCEPTED_VERSION/0" \
  -H "X-Emenda-Api-Key: $EMENDA_API_KEY" --output corrected-part-1.png
```

确认使用既有 `POST /api/intake/extractions/{id}/questions/{questionId}/review`，请求包含新 `expectedVersion`、返回的 `itemId`、`reviewStatus:2`、实际 `reviewerId`，可选 `rootCause/returnReason`。重裁本身不确认审核；漏题加入后也只需一次正常确认。

## 改判、冲突及边界

- 明确“这不是错题”时，保存纠正后的 `correct` 或 `unknown` 及真实依据；`correct` 仍要求实际作答、参考答案和证据，不用空证据绕过判错校验。已有条目身份和练习保留，沿用 `Rejected` 表示不再待审核；这不是补图请求，不生成学生补交任务。
- `404 not_found`：任务或当前题不属于该调用方，或题目不存在/已撤回。路径和正文 UUID 不同为 `400 invalid_request`。外国原图为 `400 invalid_region`。
- `409 version_conflict`：别人已修改任务。BFF 重新读取、再做用户授权和预览，不静默覆盖。
- `409 request_payload_conflict`：该任务的键已绑定不同目标、内容或操作。同键原样重放返回首次接受的目标版本，不回退当前状态。幂等键在单题和整组接口间共享，不能跨操作复用。
- 单题接口不能新增、删除、合并或拆分题目。漏题由整组接口新增 UUID；BFF 在服务端保留完整集合，绝不提交浏览器按学科筛选的部分集合。
- 每组仍最多 32 个区域；单次 JSON 最多 2 MiB。整卷题目总数没有 32 的固定限制。AI Tutor 必须升级配套实现；模型输出未完整结束会拒绝发布。
- 管理端相同能力位于 `/admin/api/extractions/{id}/questions/{questionId}`，沿用管理员会话及 CSRF。不新增浏览器服务密钥入口。
- 原图和幂等元数据保留；旧版本框/裁图快照清空。旧键重放若 `superseded=true`，`question=null`，应重新读取当前题。物理回收可能暂未完成，由持久任务重试；单题 PUT 成功不等于 S3 字节已回收。

## 联调验收

1. 混合学科试卷只提交一题重裁，返回原 `question.id/itemId` 和新版本；其他题内容、裁图和审核结果不变，响应不含其他学科。
2. 用已存在的目标确认改判，核对未发出补图请求；旧版本、其他调用方及缺少管理员 CSRF 均被拒绝。
3. 上传 40 题，核对结果及发布快照为有序 40 题；只有 incorrect 产生待审核错题。真实部署模型需单独实测，模拟结果不算准确率验收。
4. 从 32 题补漏题 33，核对原身份和审核不变，新题只确认一次。重放同请求不重复建题、重裁或重置审核。
