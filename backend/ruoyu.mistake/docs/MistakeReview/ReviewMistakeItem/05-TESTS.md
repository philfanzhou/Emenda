# ReviewMistakeItem — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。现有测试文件：待创建。

## 单元测试 — Given-When-Then 格式

### UT-01 确认审核成功（验证 SPEC FR-01/FR-04/FR-07）

- **Given**：错题 ID 有效、错题当前状态为 PendingReview、审核结果为 Confirmed（2）、reviewerId="teacher-1"、reviewComment="分类正确"。
- **When**：调用 `MistakeDomainService.ReviewMistakeItemAsync(id, 2, "teacher-1", "分类正确", null, null)`。
- **Then**：返回非 null 的 `MistakeItemModel`；`ReviewStatus == 2`；`ReviewerId == "teacher-1"`；`ReviewComment == "分类正确"`；`ReviewedAt` 接近当前时间（±5s）；`UpdatedAt` 接近当前时间（±5s）；`_processLogRepository.AddAsync` 被调用一次，传入日志的 `Action == (int)ProcessAction.ReviewConfirmed`。

### UT-02 驳回审核成功（验证 SPEC FR-04/FR-07）

- **Given**：错题当前状态为 PendingReview、审核结果为 Rejected（3）、reviewComment="图片不清晰"。
- **When**：调用 `ReviewMistakeItemAsync(id, 3, "teacher-1", "图片不清晰", null, null)`。
- **Then**：返回模型 `ReviewStatus == 3`；操作日志 `Action == (int)ProcessAction.ReviewRejected`；日志 `Details == "Mistake item rejected"`。

### UT-03 选择性更新 grade 和 subject（验证 SPEC FR-05/FR-06）

- **Given**：错题当前 `Grade = 3`、`Subject = 2`，审核参数 `grade = 8, subject = 5`。
- **When**：调用 `ReviewMistakeItemAsync(id, 2, "teacher-1", "", 8, 5)`。
- **Then**：返回模型 `Grade == 8`、`Subject == 5`。

### UT-04 grade/subject 为 0 时跳过更新（验证 SPEC FR-05/FR-06）

- **Given**：错题当前 `Grade = 3`、`Subject = 2`，审核参数 `grade = 0, subject = 0`（gRPC 层传 null）。
- **When**：调用 `ReviewMistakeItemAsync(id, 2, "teacher-1", "", null, null)`。
- **Then**：返回模型 `Grade == 3`（不变）、`Subject == 2`（不变）。

### UT-05 非待审核状态拒绝审核（验证 SPEC FR-03）

- **Given**：错题当前 `ReviewStatus = (int)ReviewStatus.Confirmed`（已确认）。
- **When**：调用 `ReviewMistakeItemAsync`。
- **Then**：抛出 `MistakeValidationException`，消息为 "只能审核待审核状态的错题"。

### UT-06 gRPC 层拒绝非法 review_status（验证 SPEC FR-02）

- **Given**：`ReviewMistakeItemRequest { ReviewStatus = ReviewStatus.Unspecified }`。
- **When**：调用 gRPC `ReviewMistakeItem` 方法。
- **Then**：抛出 `RpcException`，`StatusCode == InvalidArgument`，消息包含 "审核结果只能是确认或驳回"。

### UT-07 错题不存在返回 false（验证 SPEC FR-08）

- **Given**：Mock 仓储 `GetByIdAsync` 返回 null。
- **When**：调用 `ReviewMistakeItemAsync`。
- **Then**：返回 null；gRPC 层返回 `BoolResponse { Success = false }`。

### UT-08 事务一致性（验证 SPEC FR-07）

- **Given**：Mock `_unitOfWork.SaveChangesAsync` 抛出异常。
- **When**：调用 `ReviewMistakeItemAsync`。
- **Then**：异常向上传播；`_mistakeItemRepository.UpdateAsync` 被调用但数据未持久化。

## 集成测试

### IT-01 端到端审核流程

1. 使用真实的测试数据库上下文。
2. 先创建一条 PendingReview 状态的错题记录。
3. 调用 `ReviewMistakeItem` gRPC 方法确认审核。
4. 查询该错题，断言 `review_status == Confirmed`、`reviewer_id` 正确、`reviewed_at` 非空。
5. 查询操作日志表，断言存在 `ReviewConfirmed` 记录。

## 边界和异常测试

- EX-01 grade 传入 0 → 不更新 Grade（跳过）。
- EX-02 grade 传入 13 → 不更新 Grade（>12 无效）。
- EX-03 subject 传入 0 → 不更新 Subject（跳过）。
- EX-04 subject 传入 10 → 不更新 Subject（>9 无效）。
- EX-05 review_comment 为空字符串 → 正常更新为空。
- EX-06 reviewer_id 为 null → 正常更新为 null。

## 现有测试映射（到 SPEC 功能要求）

| 测试方法 | 验证 SPEC 项 |
| --- | --- |
| `ReviewMistakeItemAsync_Confirm` | FR-01, FR-04, FR-07 |
| `ReviewMistakeItemAsync_Reject` | FR-04, FR-07 |
| `ReviewMistakeItemAsync_OptionalFields_Update` | FR-05, FR-06 |
| `ReviewMistakeItemAsync_OptionalFields_Skip` | FR-05, FR-06 |
| `ReviewMistakeItemAsync_InvalidStatus` | FR-03 |
| `ReviewMistakeItem_InvalidReviewStatus` | FR-02 |
| `ReviewMistakeItemAsync_NotFound` | FR-08 |
| `ReviewMistakeItemAsync_TransactionFailure` | FR-07 |