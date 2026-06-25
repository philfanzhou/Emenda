# ReanalyzeMistakeItem — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：当教师对 AI 自动生成的错题根因不满意时，可触发 AI 重新分析，结合"原根因 + 教师补充描述"作为额外上下文，生成更准确的新根因。仅教师可调用，admin 不支持。

**用户故事 1（审核时）**：作为教师，我在审核错题时如果觉得 AI 的根因不对，我可以输入"学生明显审错了题目，应该是计算失误"这样的描述，让 AI 结合图片和我提供的描述重新分析，生成更准确的根因。

**用户故事 2（已确认详情中）**：作为教师，我已确认的错题经过一段时间后发现根因分析太笼统，我可以进入错题详情页，重新触发 AI 分析来优化根因，无需走驳回重传流程。

## 功能要求清单（可独立测试）

- [x] FR-01：接收 `ReanalyzeMistakeItemRequest`，包含 `id`、`reviewer_id`、可选 `teacher_description`
- [x] FR-02：验证 `id` 对应的错题存在；不存在时返回 gRPC `NotFound`
- [x] FR-03：验证 `review_status` 必须是 `PendingReview` 或 `Confirmed`；否则返回 gRPC `FailedPrecondition`
- [x] FR-04：验证错题至少有一张图片（`source_regions` 非空）；否则返回 `FailedPrecondition`
- [x] FR-05：调用 `IVlAnalyzer.ReAnalyzeAsync` 传入图片路径 + 原 root_cause + 教师描述
- [x] FR-06：VL 返回新 root_cause 后，更新 `mistake_item.root_cause` 和 `updated_at`
- [x] FR-07：写 ProcessLog（`Action=Reanalyzed(7)`），`details` 为 JSON：
  ```json
  {"previous_root_cause": "...", "teacher_description": "...", "new_root_cause": "..."}
  ```
- [x] FR-08：VL 失败时**不**更新 `root_cause`，返回 gRPC `Internal` + 错误信息
- [x] FR-09：成功返回更新后的 `MistakeItemDto`
- [x] FR-10：**不**修改 `review_status`、`subject`、`grade`、`source_regions` 等其他字段
- [x] FR-11：**不**调用 Student 服务任何接口（图片路径不变）
- [x] FR-12：teacher_portal BFF 暴露 `POST /api/mistake/{id}/reanalyze`；**admin_portal 不实现此端点**

## 详细的验收标准（可自动验证）

### AC-FR-01: 基本成功流程

- **Given** 错题 ID 存在，`review_status = PendingReview`，`source_regions` 非空，`teacher_description = "学生没读懂题意"`
- **When** 调用 `ReanalyzeMistakeItem(id, reviewer_id="t1", teacher_description="学生没读懂题意")`
- **Then** VL API 被调用，传入的图片 + previous root_cause + teacher description
- **Then** `mistake_item.root_cause` 被更新为 VL 返回的新值
- **Then** `mistake_item.updated_at` 被更新
- **Then** ProcessLog 写入一条 `Action=Reanalyzed` 记录，details 含上述三字段
- **Then** `mistake_item.review_status` **保持** `PendingReview` 不变
- **Then** 返回更新后的 `MistakeItemDto`

### AC-FR-02: 教师描述可选

- **Given** 错题 ID 存在，所有其他条件满足
- **When** 调用 `ReanalyzeMistakeItem` 不传 `teacher_description`（或传空字符串）
- **Then** 仍能正常调用 VL（prompt 中"教师描述"部分为"N/A"或省略）
- **Then** 流程其他行为不变

### AC-FR-03: 已确认状态允许重新分析

- **Given** 错题 `review_status = Confirmed`
- **When** 调用 `ReanalyzeMistakeItem`
- **Then** 正常执行（不抛异常）
- **Then** `review_status` 仍为 `Confirmed`（不回到 PendingReview）

### AC-FR-04: 已驳回状态不允许重新分析

- **Given** 错题 `review_status = Rejected`
- **When** 调用 `ReanalyzeMistakeItem`
- **Then** gRPC 返回 `FailedPrecondition`，错误信息"只能对 PendingReview 或 Confirmed 状态的错题重新分析"

### AC-FR-05: 错题不存在

- **Given** 不存在的错题 ID
- **When** 调用 `ReanalyzeMistakeItem`
- **Then** gRPC 返回 `NotFound`，错误信息"错题不存在"

### AC-FR-06: 错题无图片

- **Given** 错题 ID 存在，但 `source_regions` 为空（理论不应发生但需兜底）
- **When** 调用 `ReanalyzeMistakeItem`
- **Then** gRPC 返回 `FailedPrecondition`，错误信息"错题无图片，无法重新分析"

### AC-FR-07: VL API 失败

- **Given** 错题 ID 存在，所有其他条件满足，但 VL API 返回 500 或超时
- **When** 调用 `ReanalyzeMistakeItem`
- **Then** `mistake_item.root_cause` **保持不变**（不更新）
- **Then** gRPC 返回 `Internal`，错误信息含 VL 异常消息
- **Then** 可选：ProcessLog 仍记录 `Action=Reanalyzed`，details 含失败原因

### AC-FR-08: ID 格式无效

- **Given** `id` 是非 GUID 字符串
- **When** 调用 `ReanalyzeMistakeItem`
- **Then** gRPC 返回 `InvalidArgument`，错误信息"错题 ID 格式无效"

### AC-FR-09: ID 为空

- **Given** `id` 为空字符串
- **When** 调用 `ReanalyzeMistakeItem`
- **Then** gRPC 返回 `InvalidArgument`，错误信息"错题 ID 不能为空"

### AC-FR-10: 鉴权边界（关键约束）

- **Given** admin_portal BFF
- **When** 检查 BFF 路由表
- **Then** **不**存在 `/api/admin/mistake/{id}/reanalyze` 端点（admin 不可调用）
- **Given** teacher_portal BFF
- **When** 检查 BFF 路由表
- **Then** 存在 `POST /api/mistake/{id}/reanalyze` 端点
- **Then** 端点从 JWT 提取 `reviewer_id`（教师身份由 BFF 鉴权保证）
- **Then** 错误信息对管理员和未授权用户统一返回 401/403

## 非功能需求

| 类别 | 需求 |
|------|------|
| **性能** | VL API 调用 60 秒超时，2 次重试（与自动分析一致） |
| **可观测性** | 每次重新分析记录 INFO 级日志（含 VL 调用耗时、token 数） |
| **审计** | ProcessLog 必写，含原/新 root_cause + 教师描述 |
| **一致性** | VL 失败时 root_cause 不更新；不引入"半更新"中间态 |
| **可移植性** | 复用现有 IVlAnalyzer 接口，仅增加新方法 |

## 测试策略

- **覆盖率目标**：领域服务 `MistakeDomainService.ReanalyzeMistakeItemAsync` 行覆盖 ≥ 80%
- **必测错误路径**：
  - 错题不存在（`NotFound`）
  - 状态不允许（`FailedPrecondition`，覆盖 Rejected）
  - 无图片（`FailedPrecondition`）
  - VL 异常（`Internal`，且 root_cause 未被改）
  - ID 格式无效（`InvalidArgument`）
- **必测成功路径**：
  - 教师描述为空 → 调用成功
  - 教师描述非空 → prompt 包含此描述
  - ProcessLog 必写，details JSON 结构正确
- **测试环境要求**：`xUnit + Moq`；Mock `IVlAnalyzer`；不调用真实 VL API
- **gRPC 集成测试**：通过 `MistakeServiceImpl` 验证 gRPC 层错误码转换
- **BFF 鉴权测试**：teacher_portal BFF 测试包含端点契约；显式验证 admin_portal **不**暴露此端点

## 与相关功能的对比

| 功能 | 触发者 | 触发状态 | 输入 | 输出 | 状态变更 |
|------|--------|----------|------|------|----------|
| **ReviewMistakeItem** | 教师 | PendingReview | 审核结果（Confirmed/Rejected） | 更新后 item | Confirmed/Rejected |
| **ReanalyzeMistakeItem** | **教师** | **PendingReview / Confirmed** | 教师描述（可选） | 更新后 item | **不变** |
| **Student.ManualAnalyze** | **管理员** | UploadRecord 任意 | 无 | VL 原始结果 | 无 |

> ReanalyzeMistakeItem 与 ReviewMistakeItem **互不冲突**：可在同一 PendingReview 流程中，先重新分析再审核；也可在 Confirmed 后再优化根因。
