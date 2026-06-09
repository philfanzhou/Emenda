# 设计说明

## 1. 双向调用架构图

```
┌─────────────────────────────────────────────────────────────────┐
│                      ruoyu.student                              │
│                                                                 │
│  ┌──────────────────────┐     ┌────────────────────────────┐   │
│  │ UploadAnalysisWorker │────>│ GrpcMistakeSubmitter       │   │
│  │  (BackgroundService) │     │  (IMistakeSubmitter)       │   │
│  │                      │     │  30s timeout               │   │
│  └──────────────────────┘     └──────────┬─────────────────┘   │
│                                          │                      │
│  ┌──────────────────────┐     ┌──────────┴─────────────────┐   │
│  │ GrpcUploadDispatcher │────>│ MistakeGrpcServiceClient   │   │
│  │  (IUploadDispatcher) │     │  SubmitMistakeUpload       │──┼──┐
│  │  + Health Check      │<────│                            │   │  │
│  └──────────────────────┘     └────────────────────────────┘   │  │
│                                                                 │  │
│  ┌──────────────────────────────────────────────────────────┐   │  │
│  │ StudentLearningGrpcService (Server)                      │   │  │
│  │  RemoveImagesFromRecord <────────────────────────────────┼──┼──┤
│  └──────────────────────────────────────────────────────────┘   │  │
└─────────────────────────────────────────────────────────────────┘  │
                                                                     │
                              gRPC                                    │
                                                                     │
┌─────────────────────────────────────────────────────────────────┐  │
│                      ruoyu.mistake                              │  │
│                                                                 │  │
│  ┌──────────────────────────────────────────────────────────┐   │  │
│  │ MistakeGrpcService (Server)                              │   │  │
│  │  SubmitMistakeUpload <───────────────────────────────────┼──┼──┘
│  └──────────────────────────────────────────────────────────┘   │
│                                                                 │
│  ┌──────────────────────┐     ┌────────────────────────────┐   │
│  │ MistakeServiceImpl   │────>│ StudentLearningGrpcService │   │
│  │  CompleteUploadReview│     │  Client                    │   │
│  │                      │     │  RemoveImagesFromRecord    │───┼──┘
│  └──────────────────────┘     └────────────────────────────┘   │
│                                                                 │
│  配置: StudentGrpcService:Address = http://localhost:5005       │
└─────────────────────────────────────────────────────────────────┘
```

## 2. GrpcMistakeSubmitter 的设计

### 2.1 职责

将 VL 分析后的图片分组结果通过 gRPC 提交给 mistake 服务，创建错题条目。

### 2.2 类结构

```
IMistakeSubmitter (接口)
  ├── SubmitMistakeUploadAsync(studentId, subject, grade, imagePaths, comments, sourceUploadId, cancellationToken)
  │
  └── GrpcMistakeSubmitter (实现)
        └── 持有 MistakeGrpcService.MistakeGrpcServiceClient
```

### 2.3 关键实现细节

- **超时控制**：使用 `CancellationTokenSource.CreateLinkedTokenSource` 将外部取消令牌与 30 秒超时合并
- **comments 处理**：`null` 时转为空字符串（proto3 不支持 null）
- **imagePaths**：通过 `AddRange` 添加到 `repeated` 字段

```csharp
// 核心调用逻辑
var request = new SubmitMistakeUploadRequest
{
    StudentId = studentId,
    Subject = subject,
    Grade = grade,
    Comments = comments ?? string.Empty,
    SourceUploadId = sourceUploadId
};
request.ImagePaths.AddRange(imagePaths);

using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
cts.CancelAfter(TimeSpan.FromSeconds(30));
return await _client.SubmitMistakeUploadAsync(request, cancellationToken: cts.Token);
```

## 3. GrpcUploadDispatcher 的分发逻辑

### 3.1 职责

统一管理上传记录向 mistake 和 homework 服务的分发，并提供健康检查能力。

### 3.2 类结构

```
IUploadDispatcher (接口)
  ├── IsHomeworkServiceAvailableAsync()
  ├── IsMistakeServiceAvailableAsync()
  ├── DispatchToHomeworkAsync(studentId, subject, grade, imagePaths, comments)
  └── DispatchToMistakeAsync(studentId, subject, grade, imagePaths, comments, sourceUploadId)
  │
  └── GrpcUploadDispatcher (实现)
        ├── HomeworkGrpcService.HomeworkGrpcServiceClient
        ├── MistakeGrpcService.MistakeGrpcServiceClient
        ├── HttpClient (健康检查)
        ├── _homeworkHttpAddress
        └── _mistakeHttpAddress
```

### 3.3 健康检查逻辑

- 通过 HTTP GET 请求服务 HTTP 端点
- 3 秒超时
- 任何异常（连接失败、超时、非 2xx）均返回 `false`
- 仅 `LogDebug` 级别记录，不污染正常日志

### 3.4 DispatchToMistakeAsync 流程

```
DispatchToMistakeAsync(studentId, subject, grade, imagePaths, comments, sourceUploadId)
  │
  ├── 构建 SubmitMistakeUploadRequest
  ├── 调用 _mistakeClient.SubmitMistakeUploadAsync(request)
  │
  ├── 成功: response.Success == true
  │   └── LogInformation + return true
  │
  ├── 业务失败: response.Success == false
  │   └── LogWarning + return false
  │
  ├── RpcException
  │   └── LogError(StatusCode, Detail) + return false
  │
  └── 其他 Exception
      └── LogError + return false
```

## 4. CompleteUploadReview 中回调 student 的流程

### 4.1 触发条件

`CompleteUploadReview` 执行过程中，当以下条件同时满足时触发回调：

1. `imagePathsToRemoveFromRecord.Count > 0`（存在需要清理的图片）
2. `!string.IsNullOrEmpty(result.StudentId)`（studentId 非空）

### 4.2 流程

```
CompleteUploadReview(request)
  │
  ├── ... 审核完成业务逻辑 ...
  │
  ├── 收集 imagePathsToRemoveFromRecord（Confirmed + Rejected 的图片）
  │
  ├── 条件判断: imagePathsToRemoveFromRecord.Count > 0 && studentId 非空
  │   │
  │   ├── 构建 RemoveImagesFromRecordRequest
  │   │     ├── RecordId = request.SourceUploadId
  │   │     ├── StudentId = result.StudentId
  │   │     └── ImagePaths.AddRange(imagePathsToRemoveFromRecord)
  │   │
  │   ├── 调用 _studentClient.RemoveImagesFromRecordAsync(removeImagesRequest)
  │   │
  │   └── catch (Exception ex)
  │       └── LogError("Error removing images from upload record {RecordId}")
  │
  └── return CompleteUploadReviewResponse { Success = true, StudentId = ... }
```

### 4.3 客户端注册

```csharp
// Program.cs
var studentGrpcAddress = builder.Configuration["StudentGrpcService:Address"] ?? "http://localhost:5005";

builder.Services.AddGrpcClient<StudentLearningGrpcService.StudentLearningGrpcServiceClient>(options =>
{
    options.Address = new Uri(studentGrpcAddress);
});
```

## 5. 失败处理策略

### 5.1 student → mistake（SubmitMistakeUpload）

| 场景 | 处理方式 | 理由 |
|------|----------|------|
| 单个 group 提交失败 | catch 异常，LogError，继续处理下一个 group | 单 group 失败不应阻塞其他 group |
| 所有 group 提交失败 | 记录日志，记录仍标记为 Completed | 分析流程本身已完成，错题创建是附加操作 |
| mistake 服务不可用 | GrpcUploadDispatcher 返回 false | 调用方可据此决定是否降级 |

### 5.2 mistake → student（RemoveImagesFromRecord）

| 场景 | 处理方式 | 理由 |
|------|----------|------|
| student 回调失败 | 仅 LogError，不回滚 | **审核完成是业务关键节点**，不应因 student 临时不可用而回滚 |
| student 服务不可用 | 同上 | 图片清理可后续手动处理 |

### 5.3 不回滚的设计理由

1. **业务关键性**：审核完成（`CompleteUploadReview`）是错题审核流程的终态确认，涉及多个 mistake_item 的状态变更和 OSS 图片迁移，回滚代价极高
2. **副作用不可逆**：审核完成过程中已执行 OSS 图片迁移（`uploads/` → `mistakes/`），这些操作无法简单回滚
3. **降级可接受**：图片未从 student 记录中清理是可接受的——记录仍为 Completed 状态，不影响业务流程；可后续手动或通过补偿机制清理
4. **服务独立性**：mistake 服务不应因 student 服务的可用性而影响自身核心业务流程
