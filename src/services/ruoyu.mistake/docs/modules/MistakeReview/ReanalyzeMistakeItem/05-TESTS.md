# ReanalyzeMistakeItem — 测试策略 (TESTS)

测试工具：xUnit + Moq + FluentAssertions
现有测试文件：
- `src/services/ruoyu.mistake/src/Tests/Ruoyu.Study.Mistake.Service.Tests/MistakeServiceImplRpcTests.cs`（gRPC 层）
- `src/services/ruoyu.mistake/src/Tests/Ruoyu.Study.Mistake.Domain.Tests/MistakeDomainServiceTests.cs`（Domain 层）
- `src/services/ruoyu.mistake/src/Tests/Ruoyu.Study.Mistake.Service.Tests/VlUploadAnalyzerTests.cs`（VL analyzer）

## 单元测试 — Given-When-Then 格式

### UT-01 ReanalyzeMistakeItemAsync_WithValidId_CallsVlAndUpdatesRootCause

- **Given** 错题 ID 存在，`review_status = PendingReview`，`source_regions` 含 1 张图片
- **When** 调用 `ReanalyzeMistakeItemAsync(id, reviewerId, "学生没读懂题意")`
- **Then** 调用 `IVlAnalyzer.ReAnalyzeAsync` 一次，传入的图片路径 + 原 root_cause + 教师描述
- **Then** `item.RootCause` 更新为 VL 返回的新值
- **Then** `item.UpdatedAt` 接近 `DateTimeOffset.UtcNow`
- **Then** `item.ReviewStatus` 仍为 `PendingReview`
- **Then** ProcessLog 写入一条 `Action=Reanalyzed`，details 为合法 JSON

### UT-02 ReanalyzeMistakeItemAsync_WithEmptyTeacherDescription_CallsVlWithNullDescription

- **Given** 错题存在，无教师描述
- **When** 调用 `ReanalyzeMistakeItemAsync(id, reviewerId, null)`
- **Then** 仍调用 `IVlAnalyzer.ReAnalyzeAsync`（description 传 `null`）
- **Then** 流程其他不变

### UT-03 ReanalyzeMistakeItemAsync_WithEmptyTeacherDescription_CallsVlWithEmptyString

- **Given** 错题存在，`teacher_description = ""`
- **When** 调用 `ReanalyzeMistakeItemAsync(id, reviewerId, "")`
- **Then** 仍正常调用 VL

### UT-04 ReanalyzeMistakeItemAsync_WithConfirmedStatus_AllowsReanalysis

- **Given** 错题 `review_status = Confirmed`
- **When** 调用 `ReanalyzeMistakeItemAsync`
- **Then** 不抛异常，正常更新 root_cause
- **Then** `review_status` 仍为 `Confirmed`（不重置）

### UT-05 ReanalyzeMistakeItemAsync_WithRejectedStatus_ReturnsError

- **Given** 错题 `review_status = Rejected`
- **When** 调用 `ReanalyzeMistakeItemAsync`
- **Then** 返回 `Result { Success=false, ErrorMessage="只能对 PendingReview 或 Confirmed 状态的错题重新分析" }`
- **Then** `item.RootCause` **不变**（无任何字段被改）
- **Then** VL **未**被调用

### UT-06 ReanalyzeMistakeItemAsync_WithNonExistentId_ReturnsError

- **Given** 错题 ID 不存在
- **When** 调用 `ReanalyzeMistakeItemAsync`
- **Then** 返回 `Result { Success=false, ErrorMessage="错题不存在" }`
- **Then** VL **未**被调用
- **Then** ProcessLog **未**写入

### UT-07 ReanalyzeMistakeItemAsync_WithEmptySourceRegions_ReturnsError

- **Given** 错题 `source_regions` 为空
- **When** 调用 `ReanalyzeMistakeItemAsync`
- **Then** 返回 `Result { Success=false, ErrorMessage="错题无图片，无法重新分析" }`
- **Then** VL **未**被调用

### UT-08 ReanalyzeMistakeItemAsync_WhenVlThrows_KeepsOriginalRootCause

- **Given** 错题存在，VL 模拟抛 `InvalidOperationException`
- **When** 调用 `ReanalyzeMistakeItemAsync`
- **Then** 返回 `Result { Success=false, ErrorMessage 含 VL 异常消息 }`
- **Then** `item.RootCause` **保持原值不变**
- **Then** `item.UpdatedAt` **不变**
- **Then** VL 失败 ProcessLog **仍写入**（含失败原因）

### UT-09 ReanalyzeMistakeItemAsync_DoesNotChangeReviewStatus

- **Given** 错题 `review_status = PendingReview`
- **When** 调用 `ReanalyzeMistakeItemAsync`
- **Then** `review_status` 仍为 `PendingReview`
- **Then** `reviewer_id` 不变
- **Then** `reviewed_at` 不变

### UT-10 ReanalyzeMistakeItemAsync_DoesNotCallStudentService

- **Given** 错题存在
- **When** 调用 `ReanalyzeMistakeItemAsync`
- **Then** `IMistakePracticeRepository` / `IStudentService` 等任何外部依赖 **未**被调用
- **Then** 图片路径**不变**

## VL Analyzer 单元测试

### UT-11 VlUploadAnalyzer_ReAnalyzeAsync_BuildsCorrectPrompt

- **Given** 传入 `existingRootCause = "原 AI 分析"`, `teacherDescription = "教师补充"`, 1 张图片
- **When** 调用 `ReAnalyzeAsync`
- **Then** HTTP 请求体包含原 root_cause 文本
- **Then** HTTP 请求体包含教师描述文本
- **Then** HTTP 请求体包含 image_url
- **Then** mock HTTP 返回 `{"rootCause": "新根因"}` JSON
- **Then** 返回 `VlReanalysisResult { RootCause = "新根因" }`

### UT-12 VlUploadAnalyzer_ReAnalyzeAsync_WithEmptyTeacherDescription_OmitsNote

- **Given** `teacherDescription = null`
- **When** 调用 `ReAnalyzeAsync`
- **Then** HTTP 请求体仍可正常序列化（不抛异常）
- **Then** prompt 中不包含"teacher has provided"段

### UT-13 VlUploadAnalyzer_ReAnalyzeAsync_WhenHttpFails_ThrowsException

- **Given** HTTP 500 错误
- **When** 调用 `ReAnalyzeAsync`
- **Then** 抛 `InvalidOperationException`
- **Then** 行为与 `AnalyzeAsync` 一致（重试 + 抛异常）

## gRPC 层单元测试

### UT-14 MistakeServiceImpl_ReanalyzeMistakeItem_WithInvalidGuid_ReturnsInvalidArgument

- **Given** `request.Id = "not-a-guid"`
- **When** 调用 gRPC `ReanalyzeMistakeItem`
- **Then** 抛 `RpcException(StatusCode.InvalidArgument, "错题 ID 格式无效")`

### UT-15 MistakeServiceImpl_ReanalyzeMistakeItem_WithEmptyId_ReturnsInvalidArgument

- **Given** `request.Id = ""`
- **When** 调用 gRPC `ReanalyzeMistakeItem`
- **Then** 抛 `RpcException(StatusCode.InvalidArgument, "错题 ID 不能为空")`

### UT-16 MistakeServiceImpl_ReanalyzeMistakeItem_WithRejectedStatus_ReturnsFailedPrecondition

- **Given** Domain 返回 `Success=false, ErrorMessage="只能对 PendingReview 或 Confirmed..."`
- **When** 调用 gRPC
- **Then** 返回 `ReanalyzeMistakeItemResponse { Success=false, ErrorMessage=... }`（不抛 RpcException）

### UT-17 MistakeServiceImpl_ReanalyzeMistakeItem_WithNonExistent_ReturnsNotFound

- **Given** Domain 返回 `Success=false, ErrorMessage="错题不存在"`
- **When** 调用 gRPC
- **Then** 抛 `RpcException(StatusCode.NotFound, "错题不存在")`

### UT-18 MistakeServiceImpl_ReanalyzeMistakeItem_OnSuccess_ReturnsUpdatedItem

- **Given** Domain 成功更新
- **When** 调用 gRPC
- **Then** 返回 `ReanalyzeMistakeItemResponse { Success=true, Item=<updated Dto> }`

## Teacher Portal BFF 单元测试

### UT-19 MistakeController_Reanalyze_WithValidRequest_Returns200

- **Given** gRPC mock 返回成功
- **When** POST `/api/mistake/{id}/reanalyze?studentId={sid}` body=`{"teacherDescription": "..."}`
- **Then** 返回 200，body.success=true

### UT-20 MistakeController_Reanalyze_WithoutStudentId_Returns400

- **Given** 缺 `studentId` query param
- **When** POST `/api/mistake/{id}/reanalyze`
- **Then** 返回 400，"缺少学生ID"

### UT-21 MistakeController_Reanalyze_WhenDomainReturnsNotFound_ReturnsOkWithSuccessFalse

- **Given** gRPC 返回 `NotFound`
- **When** POST
- **Then** 返回 200，body.success=false，"错题不存在"

### UT-22 MistakeController_Reanalyze_WhenVlFails_Returns502

- **Given** gRPC 返回 `Internal`
- **When** POST
- **Then** 返回 502

## 鉴权边界测试（关键）

### UT-23 Admin_Portal_DoesNotExpose_Reanalyze_Endpoint

- **Given** admin_portal BFF 路由表
- **When** 扫描 `api/admin/mistake` 路径
- **Then** **不**存在 `{id}/reanalyze` 路由
- **说明**：此测试通过代码搜索（grep）验证，或在 admin_portal 集成测试中显式断言无此路由

### UT-24 Teacher_Portal_Exposes_Reanalyze_Endpoint

- **Given** teacher_portal BFF 路由表
- **When** POST `/api/mistake/{id}/reanalyze`
- **Then** 路由存在且 200 OK

## 覆盖目标

- Domain 层行覆盖 ≥ 80%
- gRPC 层错误码转换覆盖所有路径
- VL analyzer 必测成功/失败/空描述三个分支
- BFF 鉴权边界必须有显式测试

## 遗漏的测试场景

- 教师备注过长（>1000 字）的截断处理（本期不实现）
- 同一错题并发重新分析（依赖 EF 乐观锁，不在本期范围内）
- VL API rate limit 处理（依赖 VL 服务自身）
