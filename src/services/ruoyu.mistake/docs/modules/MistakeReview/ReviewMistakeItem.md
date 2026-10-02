# 审核错题条目

`POST /api/mistakes/{id}/review` 接收 Confirmed(2) 或 Rejected(3)、reviewerId、可空 rootCause、可选 grade/subject 与独立 returnReason。路由 id 为准，实际 PendingReview 才接受新审核；grade1–12、subject1–9 有效，其余沿既有忽略规则。reviewer 按入站身份/归属矩阵校验；受管数据始终可信认证且 reviewerId=sub。

Confirm 更新本域审核信息并记录 ReviewConfirmed，不执行图像迁移或 Student 移除。受管 item 存在退回意图时，DB 最后门禁禁止 Confirm、其它 metadata 与业务写入；Completed 不能复活。

受管 Rejected 完整流程只由 [SourceReturns](../SourceReturns.md) 定义：持久意图、独立 full 原图副本、可信 Student 历史回执、本域原子终态。保留 item/group/log/practice/创建重放及来源。只有 durable Completed data.success=true；202 pending data.success=false 与中文 ErrorMessage 保真，共享 Teacher/Assistant ReviewDetail 保留界面直到完成。

legacy 未受管 reject 保持原行为：本域审核后，图片 mistakes/→uploads、删除 item、通过 Student HTTP 创建退回记录；回调分类为成功/冲突/未知，使用固定 operationId。它没有持久 Saga 保证，异常对账/授权修复另归原实施 Issue。Student 能力禁用在 legacy reject 的任何副作用之前503；受管新退回禁用503，完成重放仍有效。reserved source-target namespace 与受管原来源拒绝 legacy mover。

响应沿旧 envelope/data.Success 与 ErrorMessage，未找到错题返回旧失败语义；managed分支错误分类与 removedImagePaths 空集合见 SourceReturns。rootCause 为错因、returnReason 为学生看到的退回原因，不混合写入 comments。

验证沿 `MistakeDomainServiceTests`、`ReviewRejectCallbackTests`、`SourceReturnPostgresTests`、`SourceReturnS3Tests` 和共享 BFF 响应消费回归。运行目标 solution Release build/test；数据库与实际S3专项环境见 SourceReturns，未执行不算通过。
