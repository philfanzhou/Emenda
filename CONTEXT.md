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
调用方提交的一组有序原图经 StructaDoc 版面分析与 AI Tutor 判错后的持久任务；只有判为 incorrect 的题目生成待审核 Mistake Item，原图与每个修订版本均保留。

**Conversation Collection**:
从 AI Tutor 对话中采集错题的持久作业；生成的 Mistake Item 携带独立的证据快照，不依赖对话历史。
