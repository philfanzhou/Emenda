# AddMistakePractice - 功能规格

## 功能概述

学生对已确认的错题进行练习，记录练习答案、是否正确及可选的图片路径。系统验证错题状态后创建练习记录并记录操作日志。

## 用户故事

作为学生，我希望能够对已确认的错题进行练习并记录结果，以便追踪我的错题掌握进度。

## 功能要求

### FR-01 验证错题存在性

系统根据 `mistake_item_id` 查询错题条目，若不存在则抛出 `MistakeValidationException("错题条目不存在")`。

### FR-02 验证错题审核状态

系统验证错题的 `ReviewStatus` 必须为 `Confirmed`，否则抛出 `MistakeValidationException("只能对已确认的错题进行练习")`。

### FR-03 创建练习记录

系统自动设置：
- `Id` = `Guid.NewGuid()`
- `PracticeDate` = `DateTimeOffset.UtcNow`

### FR-04 可选图片路径

当请求中包含 `image_paths` 且非空时，将其赋值给练习记录的 `ImagePaths`。

### FR-05 记录操作日志

创建成功后，系统自动记录一条 ProcessLog：
- `Action` = `Practiced` (5)
- `Details` = `"Student practiced mistake"`

### FR-06 错误处理

- `MistakeValidationException` → `RpcException(StatusCode.InvalidArgument)`

## 验收标准

### AC-FR-01

**Given** 请求中提供了不存在的 mistake_item_id
**When** 调用 AddMistakePractice RPC
**Then** 返回 gRPC 错误，状态码为 `InvalidArgument`，消息为"错题条目不存在"

### AC-FR-02

**Given** 错题条目存在但 ReviewStatus 为 PendingReview
**When** 调用 AddMistakePractice RPC
**Then** 返回 gRPC 错误，状态码为 `InvalidArgument`，消息为"只能对已确认的错题进行练习"

### AC-FR-03

**Given** 错题条目 ReviewStatus 为 Confirmed，请求包含有效的练习数据
**When** 调用 AddMistakePractice RPC
**Then** 返回 `MistakePracticeDto`，包含系统生成的 Id 和当前 UTC 时间作为 PracticeDate

### AC-FR-04

**Given** 请求中包含 image_paths 列表
**When** 调用 AddMistakePractice RPC
**Then** 返回的 MistakePracticeDto 中包含对应的 image_paths

### AC-FR-05

**Given** 成功添加了一条练习记录
**When** 查询该错题的 ProcessLog
**Then** 存在一条 Action=Practiced、Details="Student practiced mistake" 的日志记录

## 非功能需求

- 单次练习记录创建应在 500ms 内完成
- 错题状态验证防止对未审核数据产生练习记录

## 测试策略

- 单元测试：验证领域服务 `AddPracticeAsync` 的状态验证和记录创建逻辑
- 集成测试：验证 gRPC 层的请求映射和错误转换
