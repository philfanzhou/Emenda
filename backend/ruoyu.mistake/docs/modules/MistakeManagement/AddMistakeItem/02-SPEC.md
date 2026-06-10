# AddMistakeItem - 功能规格

## 功能概述

教师通过 gRPC 接口手动添加错题条目，指定学生、学科、年级、来源上传记录及图片区域信息。系统自动设置审核状态和创建时间，并记录操作日志。

## 用户故事

作为教师，我希望能够手动添加错题条目，以便将学生试卷中截取的错题区域录入系统进行后续审核和练习。

## 功能要求

### FR-01 接受手动添加错题请求

系统接受 `AddMistakeItemRequest`，包含 `student_id`、`subject`、`grade`、`source_upload_id`、`source_regions` 和 `comments` 字段。

### FR-02 验证输入数据

系统验证以下规则：
- `student_id` 非空
- `subject` 范围 0-9
- `grade` 范围 0-12
- `source_upload_id` 非空
- `source_regions` 非空（至少包含一个图片区域）

验证失败时抛出 `MistakeValidationException`，gRPC 层转换为 `StatusCode.InvalidArgument`。

### FR-03 自动设置默认值

系统自动设置：
- `Id` = `Guid.NewGuid()`
- `ReviewStatus` = `PendingReview` (1)
- `CreatedAt` = `DateTimeOffset.UtcNow`

### FR-04 记录操作日志

创建成功后，系统自动记录一条 ProcessLog：
- `Action` = `Created` (1)
- `Details` = `"Mistake item manually added by teacher"`

### FR-05 映射图片区域

gRPC 层将 `SourceRegion` proto 消息映射为 `SourceRegionModel`，包含 `SourceImagePath` 和可选的 `BoundingBox`（含 X1、Y1、X2、Y2 坐标）。

## 验收标准

### AC-FR-01

**Given** 教师构造了一个包含有效字段和至少一个 SourceRegion 的 AddMistakeItemRequest
**When** 调用 AddMistakeItem RPC
**Then** 返回 `MistakeItemDto`，包含系统生成的 Id 和 `PendingReview` 审核状态

### AC-FR-02

**Given** 教师构造了一个缺少 student_id 的 AddMistakeItemRequest
**When** 调用 AddMistakeItem RPC
**Then** 返回 gRPC 错误，状态码为 `InvalidArgument`，消息包含"缺少学生ID"

### AC-FR-03

**Given** 教师构造了一个有效的 AddMistakeItemRequest
**When** 调用 AddMistakeItem RPC
**Then** 返回的 MistakeItemDto 中 `review_status` 为 `PENDING_REVIEW`，`created_at` 为当前 UTC 时间

### AC-FR-04

**Given** 教师成功添加了一条错题
**When** 查询该错题的 ProcessLog
**Then** 存在一条 Action=Created、Details="Mistake item manually added by teacher" 的日志记录

## 非功能需求

- 无特殊性能要求，单次创建操作应在 500ms 内完成
- 验证错误消息使用中文，便于前端展示

## 测试策略

- 单元测试：验证领域服务 `AddMistakeItemAsync` 的验证逻辑、默认值设置和 ProcessLog 记录
- 集成测试：验证 gRPC 层的请求映射和错误转换
