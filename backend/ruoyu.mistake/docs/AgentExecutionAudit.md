# Agent Execution Audit

## 1. 仍需人工介入的功能点

无。

## 2. 风险与人工后续动作

### 非事务性图片迁移风险（已验证，需人工评估是否加强）

**调用链路**：
- `ReviewMistakeItem`（gRPC 层）：审核确认后，逐条迁移该错题的图片 `uploads/ → mistakes/`
- `CompleteUploadReview`（gRPC 层）：批量审核完成后，遍历所有 Confirmed 错题，逐条迁移图片

**具体行为**：
1. 对每张图片执行 `CopyObjectAsync(oldPath, newPath)` + `DeleteAsync(oldPath)`，两步在同一 try 块内
2. 如果 Copy 成功但 Delete 失败：OSS 中同时存在 uploads/ 和 mistakes/ 的副本，DB 路径不更新（保留 uploads/），下次 CompleteUploadReview 会重试
3. 如果 Copy 失败：OSS 中只有 uploads/ 副本，DB 路径不更新，下次 CompleteUploadReview 会重试
4. 单图失败不阻塞其他图片迁移，整体流程仍返回 Success=true
5. CompleteUploadReview 使用 pathMapping 缓存去重，同一 uploads/ 路径只迁移一次

**已覆盖的 UT（10 个 Service 层测试）**：
- ReviewMistakeItem：确认迁移、拒绝不迁移、Copy 失败容错、已迁移路径跳过、Delete 失败路径不更新
- CompleteUploadReview：仅迁移 Confirmed 项、Copy 失败容错、路径去重缓存、待审核项返回错误、全量 OSS 失败仍返回成功

**人工评估建议**：
- 当前"失败保留原路径 + 下次重试"策略在低频操作场景下可接受
- 如需更强一致性，可考虑：引入迁移状态字段、补偿重试队列、或 Copy 前先检查目标路径是否已存在
