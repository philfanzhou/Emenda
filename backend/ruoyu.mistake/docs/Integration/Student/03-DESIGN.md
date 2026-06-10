# 设计说明

## 1. 单向调用架构图

mistake 与 student 之间是单向 gRPC 调用：仅 student → mistake 方向存在 gRPC 通信。mistake 不调用 student 的任何 gRPC 接口，仅通过 `student_id` 字段只读引用学生数据。

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
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ 数据引用层                                                │   │
│  │  mistake_item.student_id  ──只读引用──>  ruoyu.student   │   │
│  │  mistake_practice.student_id ──只读引用──>  ruoyu.student│   │
│  │  （无 gRPC 调用，无外键约束，不校验学生是否存在）         │   │
│  └──────────────────────────────────────────────────────────┘   │
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

## 4. student_id 引用设计

### 4.1 引用方式

mistake 服务不注册任何 student gRPC 客户端，不调用 student 的任何 gRPC 方法。`student_id` 是只读引用字段，其数据来源于 student 服务调用 `SubmitMistakeUpload` 时传入的参数。

### 4.2 引用字段

| 表 | 字段 | 语义 |
|----|------|------|
| `mistake_item` | `student_id` | 由 SubmitMistakeUpload 请求传入，mistake 仅存储 |
| `mistake_practice` | `student_id` | 由 AddMistakePractice 请求传入，mistake 仅存储 |

### 4.3 设计决策

| 决策 | 理由 |
|------|------|
| 不校验学生是否存在 | mistake 服务不应依赖 student 服务的可用性；`student_id` 是引用语义，非关联语义 |
| 无外键约束 | mistake 与 student 使用不同数据库，无法跨库外键 |
| 不注册 gRPC 客户端 | mistake 不需要主动查询 student 数据，避免不必要的耦合 |
| 不回调 student | mistake 的核心业务流程不应因 student 服务不可用而受影响 |

## 5. 失败处理策略

### 5.1 student → mistake（SubmitMistakeUpload）

| 场景 | 处理方式 | 理由 |
|------|----------|------|
| 单个 group 提交失败 | catch 异常，LogError，继续处理下一个 group | 单 group 失败不应阻塞其他 group |
| 所有 group 提交失败 | 记录日志，记录仍标记为 Completed | 分析流程本身已完成，错题创建是附加操作 |
| mistake 服务不可用 | GrpcUploadDispatcher 返回 false | 调用方可据此决定是否降级 |
