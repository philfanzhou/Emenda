# ReanalyzeMistakeItem — 约定与规范 (CONVENTIONS)

## 命名约定

| 元素 | 规范 | 示例 |
|------|------|------|
| gRPC RPC | `ReanalyzeMistakeItem` | `rpc ReanalyzeMistakeItem(...)` |
| gRPC Request | `ReanalyzeMistakeItemRequest` | |
| gRPC Response | `ReanalyzeMistakeItemResponse` | |
| gRPC field | `snake_case` | `teacher_description` |
| C# Method | PascalCase + Async 后缀 | `ReanalyzeMistakeItemAsync` |
| C# Field | PascalCase | `TeacherDescription` |
| ProcessAction enum | `PROCESS_ACTION_REANALYZED = 7` | |
| ProcessLog details | JSON 字符串 | `{"previous_root_cause":"...","teacher_description":"...","new_root_cause":"..."}` |
| BFF endpoint | `POST /api/mistake/{id}/reanalyze` | |
| BFF Request body | `ReanalyzeMistakeRequest` | `{ "teacherDescription": "..." }` |
| BFF Response | `ReanalyzeMistakeResponse` | `{ "success": true, "data": {...} }` |
| Vue component | ReanalyzeButton / ReanalyzeModal | |
| 错误信息 | 中文（业务域） | "只能对 PendingReview 或 Confirmed 状态的错题重新分析" |
| 日志消息 | 英文（系统日志） | "ReanalyzeMistakeItem: VL returned new root cause for {ItemId}" |

## 错误信息规范

| 触发条件 | 错误信息 | gRPC Code |
|----------|----------|-----------|
| ID 为空 | "错题 ID 不能为空" | `InvalidArgument` |
| ID 格式无效 | "错题 ID 格式无效" | `InvalidArgument` |
| 错题不存在 | "错题不存在" | `NotFound` |
| 状态不允许 | "只能对 PendingReview 或 Confirmed 状态的错题重新分析" | `FailedPrecondition` |
| 无图片 | "错题无图片，无法重新分析" | `FailedPrecondition` |
| VL 失败 | "重新分析失败：{VL 异常消息}" | `Internal` |

## 日志规范

```csharp
// 成功
_logger.LogInformation(
    "ReanalyzeMistakeItem: ItemId={ItemId}, ReviewerId={ReviewerId}, " +
    "PreviousRootCauseLength={PrevLength}, NewRootCauseLength={NewLength}, " +
    "TeacherDescriptionProvided={HasNote}, Duration={DurationMs}ms",
    itemId, reviewerId, prevLen, newLen, hasNote, durationMs);

// 失败
_logger.LogWarning(
    "ReanalyzeMistakeItem failed: ItemId={ItemId}, ErrorCode={ErrorCode}, Reason={Reason}",
    itemId, errorCode, reason);
```

**禁止**：
- 禁止记录 root_cause 完整内容（可能含敏感学生信息）
- 禁止记录教师描述完整内容（可能含敏感教学反馈）
- 仅记录**长度**和**是否提供**即可

## VL Prompt 规范

- 必须**明确说明是重新分析**（"You are re-analyzing..."）
- 必须**引用原 root_cause**作为参考
- 教师描述若有，**作为补充上下文**（"additional context"）
- 输出**仅**JSON `{"rootCause": "..."}`，无其他文字
- 中文根因，**不超过 50 字符**（与自动分析一致）
- 不修改 subject/grade（重新分析仅关注根因）

## 鉴权边界规范

| 调用方 | 允许 | 说明 |
|--------|------|------|
| Teacher Portal BFF | ✅ 允许 | 端点契约保证 |
| Admin Portal BFF | ❌ **不实现** | 根据用户要求 |
| Student Portal BFF | ❌ 不适用 | 学生不可重新分析 |
| Mistake 服务 gRPC | ✅ 暴露 | 不鉴权角色，由 BFF 端点契约保证 |

## 关键设计规范

1. **不重置 ReviewStatus**：重新分析 ≠ 重新审核
2. **不持久化 teacher_description**：仅在 ProcessLog 保留审计
3. **不修改 subject/grade/source_regions**：仅更新 root_cause
4. **VL 失败保留原值**：业务连续性 > 提示错误
5. **必写 ProcessLog**：审计要求
6. **不调用 Student 服务**：图片路径不变
7. **不引入新依赖**：复用现有 IVlAnalyzer + MistakeItemModel.RootCause

## 兼容性规范

- proto tag 7（ProcessAction）**已存在**（PROCESS_ACTION_MASTERED=6），新值 = 7 顺序追加
- 新 RPC 方法为**纯新增**（不修改现有）
- 新 BFF 端点为**纯新增**（不修改现有）
- 现有 6 个 ProcessAction 值**不变**，向前兼容

## 安全规范

- **PII 处理**：root_cause / teacher_description 可能含 PII，仅在 BFF 透传，不写日志
- **注入防护**：teacher_description 拼入 prompt 之前**不**做转义（VL 模型按自然语言处理），但 BFF 端应对长度做上限校验（建议 ≤ 500 字）
- **认证**：BFF 端点从 JWT 提取 reviewer_id，不接受请求体传入（防伪）
