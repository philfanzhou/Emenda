# 审核错题条目

`POST /api/mistakes/{id}/review` 接收 Confirmed(2) 或 Rejected(3)、reviewerId、可空 rootCause、可选 grade/subject 与独立 returnReason。路由 id 为准，实际 PendingReview 才接受新审核；grade1–12、subject1–9 有效，其余沿既有忽略规则。调用方必须是携带 `X-Emenda-Api-Key` 的产品调用方，由调用方负责学生与审核人授权。

Confirm 更新本域审核信息并记录 ReviewConfirmed，不移动图片。

Rejected 走本域本地路径：持久化 Rejected 状态与 ReviewRejected 日志，图片留在 `mistakes/` 原位，不创建外部退回记录、不发回调、不复制/删除 OSS 对象；成功响应与 Confirm 相同（HTTP 200，`data.Success=true`）。

两类条目有专门规则：

- 自动提取生成的条目（`sourceUploadId` 为 `extract:{jobId}`）必须通过 `/api/intake/extractions/{id}/questions/{questionId}/review` 带版本号审核，本路由返回 409 `extraction_review_endpoint_required`。
- 对话采集条目只允许所属产品调用方审核（否则 401）；rootCause 缺省时保留原值，returnReason 写入 ReviewRejected 日志（最多 2000 字符）。

响应沿 envelope/data.Success 与 ErrorMessage，未找到错题返回 `success=false`。rootCause 为错因、returnReason 为学生看到的退回原因，不混合写入 comments。

验证见 `MistakeDomainServiceTests`、`StandaloneReviewRejectTests`、`ExtractionReviewHttpPostgresTests` 与 `ConversationIsolationHttpPostgresTests`。
