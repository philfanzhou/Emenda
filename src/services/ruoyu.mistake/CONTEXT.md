# Mistake Learning

Mistake Learning 定义学习者特定错题的审核、练习和掌握过程。可复用题目内容属于 Question Catalog。

## Language

**Mistake Item**:
某个 Student 在学习中产生并需要追踪的错误记录。
_Avoid_: Question、Upload Record

**Source Upload**:
Mistake Item 所依据的 Learner Upload Record 引用。
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

**Source Intake**:
对一份不可变 Source Upload 内容取得的持久处理归属与题目分组结果。来源仍保留，物化错题不等于完成跨上下文交接。

**Source Group**:
同一不可变来源中，归属于一题的合法原图或整数区域集合。集合顺序不改变业务身份，同一区域不能被重新分配到另一题。

**Source Return**:
对一项受管错题取得的持久退回归属，以原学生提交证明与保留历史的本域退回事实共同确认完成。它不代表来源释放或当前退回图片仍未被学生消费。

**Source Handoff**:
完整封口来源的所有错题组终结、目标持久且Learner已条件消费后，由原提交证明与本域永久完成事实共同确认的交接。来源历史仍保留，不授予物理对象释放。
_Avoid_: Target Ready、Source Release
