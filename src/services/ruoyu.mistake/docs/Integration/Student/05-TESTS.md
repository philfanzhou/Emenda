# 测试用例

## 1. 正常创建错题（student → mistake）

### TC-01: VL 分析完成后自动创建错题

- **前置条件**：student 和 mistake 服务均正常运行；上传记录包含多张图片
- **步骤**：
  1. 创建上传记录，状态为 Pending
  2. UploadAnalysisWorker 拾取记录，执行 VL 分析
  3. VL 返回多个 group（不同 subject/grade）
- **预期**：
  - 每个 group 独立调用 `SubmitMistakeUpload`
  - 所有调用返回 `success=true`
  - `created_item_ids` 非空
  - 日志记录 `VL created {CreatedCount} of {GroupCount} mistake items`

### TC-02: DispatchToMistakeAsync 正常分发

- **前置条件**：mistake 服务可用
- **步骤**：
  1. 调用 `GrpcUploadDispatcher.DispatchToMistakeAsync` 传入合法参数
- **预期**：
  - 返回 `true`
  - 日志记录 `Dispatched upload record to mistake service: {ItemCount} items created`

## 2. 参数非法场景

### TC-03: 空 studentId

- **步骤**：调用 `SubmitMistakeUpload`，`studentId` 为空字符串
- **预期**：返回 `success=false`，`error_message` 包含 studentId 相关错误信息

### TC-04: 无效 subject

- **步骤**：调用 `SubmitMistakeUpload`，`subject` 为不在枚举范围内的值（如 999）
- **预期**：返回 `success=false` 或 gRPC `INVALID_ARGUMENT`

### TC-05: 无效 grade

- **步骤**：调用 `SubmitMistakeUpload`，`grade` 为不在枚举范围内的值（如 999）
- **预期**：返回 `success=false` 或 gRPC `INVALID_ARGUMENT`

### TC-06: 空 imagePaths

- **步骤**：调用 `SubmitMistakeUpload`，`imagePaths` 为空列表
- **预期**：返回 `success=false`，`error_message` 包含图片路径相关错误信息

### TC-07: comments 为 null

- **步骤**：调用 `GrpcMistakeSubmitter.SubmitMistakeUploadAsync`，`comments` 传 `null`
- **预期**：正常处理，`comments` 被转为空字符串

## 3. student_id 引用验证

### TC-08: student_id 仅存储不校验

- **前置条件**：mistake 服务正常运行
- **步骤**：
  1. 调用 `SubmitMistakeUpload`，传入一个不存在的 `studentId`（如 `nonexistent-student-id`）
- **预期**：
  - 创建成功，`success=true`
  - mistake_item 表中 `student_id` 字段存储了传入的值
  - mistake 不校验该 ID 是否对应有效学生

### TC-09: student_id 用于查询和分组

- **前置条件**：mistake 服务中存在多条错题记录
- **步骤**：
  1. 调用 `GetMistakeItemList`，按 `student_id` 筛选
  2. 调用 `GetPendingReviewUploads`，确认结果按 `student_id` 分组
- **预期**：
  - 查询结果正确按 `student_id` 过滤
  - 分组结果正确包含 `student_id` 字段

## 4. source_upload_id 重复提交

### TC-10: 相同 sourceUploadId 重复调用

- **步骤**：
  1. 使用 `sourceUploadId = "abc-123"` 调用 `SubmitMistakeUpload`，成功
  2. 使用相同 `sourceUploadId = "abc-123"` 再次调用 `SubmitMistakeUpload`
- **预期**：
  - 两次调用均返回 `success=true`
  - 产生两批不同的 `created_item_ids`
  - 证明接口非幂等

### TC-11: 不同 group 使用相同 sourceUploadId

- **步骤**：VL 分析返回多个 group，所有 group 使用同一个 `record.Id.ToString()` 作为 `sourceUploadId`
- **预期**：
  - 每个 group 独立创建错题条目
  - 所有条目的 `source_upload_id` 相同
  - 不影响创建结果

## 5. 超时场景

### TC-12: GrpcMistakeSubmitter 30 秒超时

- **前置条件**：mistake 服务响应延迟超过 30 秒
- **步骤**：
  1. 通过 `GrpcMistakeSubmitter.SubmitMistakeUploadAsync` 发送请求
- **预期**：
  - 30 秒后抛出 `OperationCanceledException` 或 `RpcException(DeadlineExceeded)`
  - `UploadAnalysisWorker` 捕获异常并 LogError
  - 其他 group 继续处理

### TC-13: 健康检查超时

- **前置条件**：mistake 服务 HTTP 端点响应超过 3 秒
- **步骤**：调用 `IsMistakeServiceAvailableAsync()`
- **预期**：
  - 3 秒后返回 `false`
  - LogDebug 记录健康检查失败

## 6. 单 group 失败隔离

### TC-14: 多 group 中部分提交失败

- **前置条件**：VL 分析返回 3 个 group
- **步骤**：
  1. group-1 提交成功
  2. group-2 提交失败（如 mistake 服务内部错误）
  3. group-3 提交成功
- **预期**：
  - group-1 和 group-3 正常创建错题
  - group-2 失败被 catch，LogError
  - 日志记录 `VL created 2 of 3 mistake items`
  - 上传记录仍标记为 Completed
