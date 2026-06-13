# 集成约定

## 1. student_id 引用约定

### 1.1 引用语义

- `student_id` 是只读引用字段，mistake 不校验对应学生是否存在
- `student_id` 由 student 服务在调用 `SubmitMistakeUpload` 时传入，mistake 仅存储
- mistake 不注册任何 student gRPC 客户端，不调用 student 的任何 gRPC 方法

### 1.2 引用字段清单

| 表 | 字段 | 索引 |
|----|------|------|
| `mistake_item` | `student_id VARCHAR(36) NOT NULL` | `IX_mistake_item_student_id`、复合索引 |
| `mistake_practice` | `student_id VARCHAR(36) NOT NULL` | `IX_mistake_practice_student_id` |

### 1.3 约定

- 不对 `student_id` 设置外键约束（跨服务不同数据库）
- 不在 mistake 侧校验 `student_id` 对应的学生是否存在
- `student_id` 用于查询筛选和分组，不用于关联查询

## 2. gRPC 客户端配置约定（student 侧）

### 2.1 客户端注册

- 使用 `AddGrpcClient<T>()` 注册 gRPC 客户端，不手动创建 `Channel` 和 `Client`
- 地址通过配置键注入，不硬编码

### 2.2 客户端生命周期

- gRPC 客户端由 DI 容器管理，默认为 Singleton 或 Transient（由 `AddGrpcClient` 决定）
- 不在代码中手动 `Dispose` 客户端

## 3. 超时约定

| 场景 | 超时 | 方式 |
|------|------|------|
| `GrpcMistakeSubmitter.SubmitMistakeUploadAsync` | 30 秒 | `CancellationTokenSource.CancelAfter(30s)` + `CreateLinkedTokenSource` |
| `GrpcUploadDispatcher.DispatchToMistakeAsync` | 默认 | 使用 gRPC 客户端默认超时 |
| `GrpcUploadDispatcher.DispatchToHomeworkAsync` | 默认 | 使用 gRPC 客户端默认超时 |
| 健康检查（HTTP） | 3 秒 | `CancellationTokenSource(3s)` |

### 约定

- 需要精确控制超时的场景使用 `CancellationTokenSource`，不依赖 gRPC Deadline
- 外部 `CancellationToken` 必须通过 `CreateLinkedTokenSource` 合并，确保调用方取消时也能及时终止
- 健康检查使用较短超时（3 秒），避免阻塞主流程

## 4. 错误处理约定

### 4.1 student → mistake

| 组件 | 错误处理方式 |
|------|-------------|
| `GrpcMistakeSubmitter` | 异常向上传播，不做 catch |
| `GrpcUploadDispatcher.DispatchToMistakeAsync` | catch 所有异常，返回 `false` |
| `UploadAnalysisWorker` | catch 单 group 异常，LogError，继续处理下一个 group |

### 4.2 约定

- **单 group 隔离**：student → mistake 的单 group 失败不影响其他 group
- **异常日志级别**：
  - `RpcException`：`LogError`，包含 `StatusCode` 和 `Detail`
  - 业务失败（`success=false`）：`LogWarning`
  - 健康检查失败：`LogDebug`
  - 其他异常：`LogError`

## 5. 日志约定

### 5.1 日志级别

| 场景 | 级别 |
|------|------|
| 成功创建错题 | `LogInformation` |
| 成功分发到 mistake | `LogInformation` |
| 业务失败（`success=false`） | `LogWarning` |
| gRPC 调用异常 | `LogError` |
| 健康检查失败 | `LogDebug` |

### 5.2 日志消息格式

- 使用结构化日志（`LogInformation("... {RecordId} ...", recordId)`）
- 包含关键业务标识：`RecordId`、`StudentId`、`SourceUploadId`
- gRPC 错误包含 `StatusCode` 和 `Detail`

### 5.3 日志示例

```
// 成功
VL created mistake item(s) item-1,item-2 for record abc-123, group indices=0,1
Dispatched upload record to mistake service: 2 items created

// 业务失败
VL failed to create mistake for record abc-123, group indices=0,1: Invalid subject

// gRPC 异常
gRPC error dispatching upload record to mistake service: Unavailable - Connection refused
```

## 6. 配置键命名约定

### 6.1 格式

`{ServiceName}GrpcService:Address`

### 6.2 当前配置键

| 配置键 | 使用方 | 默认值 | 说明 |
|--------|--------|--------|------|
| `MistakeGrpcService:Address` | ruoyu.student | — | mistake 服务 gRPC 地址 |
| `HomeworkGrpcService:Address` | ruoyu.student | — | homework 服务 gRPC 地址 |

### 6.3 约定

- 配置键使用 PascalCase，层级用 `:` 分隔
- 必须提供默认值（至少 `http://localhost:{port}`）
- 默认值仅在开发环境可用，生产环境必须显式配置
- 通过 `builder.Configuration["Key"] ?? defaultValue` 读取
