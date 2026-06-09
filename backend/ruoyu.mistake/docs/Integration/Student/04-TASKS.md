# 集成任务清单

## 1. 配置任务

### 1.1 配置 StudentGrpcService:Address

- **服务**：ruoyu.mistake
- **配置键**：`StudentGrpcService:Address`
- **默认值**：`http://localhost:5005`
- **操作**：在 `appsettings.json` 或环境变量中设置正确的 student 服务 gRPC 地址
- **验证**：启动 mistake 服务，确认日志中无 gRPC 连接错误

### 1.2 配置 MistakeGrpcService:Address

- **服务**：ruoyu.student
- **操作**：确认 `AddGrpcClient<MistakeGrpcService.MistakeGrpcServiceClient>` 配置的地址正确
- **验证**：启动 student 服务，确认 gRPC 客户端可连接 mistake 服务

## 2. 连通性验证

### 2.1 验证 student → mistake 连通性

- [ ] 启动 mistake 服务
- [ ] 启动 student 服务
- [ ] 调用 `GrpcUploadDispatcher.IsMistakeServiceAvailableAsync()` 返回 `true`
- [ ] 或通过 `DispatchToMistakeAsync` 发送测试请求，确认无 `UNAVAILABLE` 错误

### 2.2 验证 mistake → student 连通性

- [ ] 启动 student 服务
- [ ] 启动 mistake 服务
- [ ] 执行 `CompleteUploadReview`，观察日志中 `RemoveImagesFromRecordAsync` 调用结果
- [ ] 确认无 `UNAVAILABLE` 错误

## 3. 功能测试

### 3.1 测试 SubmitMistakeUpload 正常流程

- [ ] 创建上传记录，触发 VL 分析
- [ ] 确认分析完成后自动调用 `SubmitMistakeUpload`
- [ ] 确认 mistake 服务创建了对应的错题条目
- [ ] 确认返回的 `created_item_ids` 非空

### 3.2 测试 CompleteUploadReview 中 student 回调

- [ ] 创建错题上传记录并审核
- [ ] 执行 `CompleteUploadReview`
- [ ] 确认 `RemoveImagesFromRecordAsync` 被调用
- [ ] 确认 student 侧上传记录中的图片被移除

### 3.3 测试 student 不可用时的降级行为

- [ ] 停止 student 服务
- [ ] 执行 `CompleteUploadReview`
- [ ] 确认审核完成仍然返回 `success=true`
- [ ] 确认日志中记录了 `RemoveImagesFromRecordAsync` 调用失败
- [ ] 确认 mistake 侧审核状态已正确更新

### 3.4 测试 mistake 不可用时的降级行为

- [ ] 停止 mistake 服务
- [ ] 触发 VL 分析流程
- [ ] 确认 `SubmitMistakeUpload` 调用失败被捕获
- [ ] 确认上传记录仍标记为 Completed
- [ ] 确认其他 group 的提交尝试不受影响

## 4. 边界场景测试

### 4.1 source_upload_id 重复提交

- [ ] 使用相同 `source_uploadId` 连续调用两次 `SubmitMistakeUpload`
- [ ] 确认两次调用均成功，创建了两批错题条目（非幂等行为）

### 4.2 超时场景

- [ ] 模拟 mistake 服务响应延迟超过 30 秒
- [ ] 确认 `GrpcMistakeSubmitter` 在 30 秒后抛出 `OperationCanceledException`
- [ ] 确认 `UploadAnalysisWorker` 捕获异常并记录日志

### 4.3 参数非法

- [ ] 空 `studentId` 调用 `SubmitMistakeUpload`
- [ ] 无效 `subject`/`grade` 值调用
- [ ] 空 `imagePaths` 调用
- [ ] 确认返回 `success=false` 并包含有意义的 `error_message`

## 5. 部署前检查

- [ ] 确认 `StudentGrpcService:Address` 在生产环境配置正确
- [ ] 确认 `MistakeGrpcService:Address` 在生产环境配置正确
- [ ] 确认两个服务的 gRPC 端口在网络策略中放行
- [ ] 确认健康检查端点可达
