# Mistake Learning

Mistake Learning 定义学习者特定错题的审核、练习和掌握过程。可复用题目内容属于 Question Catalog。

## Language

**Mistake Item**:
某个 Student 在学习中产生并需要追踪的错误记录。
_Avoid_: Question、Upload Record

**Source Upload**:
Mistake Item 所依据的来源引用（自动提取为 `extract:{jobId}`）。
_Avoid_: Upload Record Ownership、Document File

**Review**:
对 Mistake Item 内容和有效性作出的确认或退回决定。
_Avoid_: Homework Review、Storage Resolution

**Practice**:
围绕已确认 Mistake Item 进行的后续练习记录。
_Avoid_: Question、Homework Submission

**Root Cause**:
对 Student 产生该 Mistake Item 原因的领域描述。
_Avoid_: Error Type、Process Log

**Extraction Job**:
调用方提交的一组有序原图经 StructaDoc 版面分析与 AI Tutor 判错后的持久任务。原图及已接受补图长期保留；只有当前 incorrect 且未删除的题目生成正式裁图及待审核 Mistake Item。修订发布后旧版本只保留操作回执，不保留旧框和旧裁图快照；失败的物理回收由持久任务继续处理。

**Extraction Proposal**:
基于当前版本、原图和人工说明生成的待采纳建议。可以继续说明、微调或丢弃；生成建议不生成正式裁图、不创建错题、不改变审核。明确采纳且版本校验通过后才发布正式结果。

**Extraction Supplement**:
绑定原 Extraction Job、原题和原 Mistake Item 的补图请求及接收回执。使用 PendingReview 和可读说明表示待补资料；接受补图不新增错题，也不自动确认审核。

**Conversation Collection**:
从 AI Tutor 对话中采集错题的持久作业；生成的 Mistake Item 携带独立的证据快照，不依赖对话历史。
