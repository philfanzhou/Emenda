# 接口规格

## 1. SubmitMistakeUpload（student → mistake）

### 1.1 请求参数

| 字段 | 类型 | 必填 | 取值范围 | 业务含义 |
|------|------|------|----------|----------|
| `student_id` | `string` | 是 | 非空 GUID | 学生唯一标识 |
| `subject` | `int32` | 是 | 0-9（见 Subject 枚举） | 科目：0=未指定, 1=语文, 2=数学, 3=英语, 4=物理, 5=化学, 6=生物, 7=历史, 8=地理, 9=政治 |
| `grade` | `int32` | 是 | 0-12（见 Grade 枚举） | 年级：0=未指定, 1-6=小学, 7-9=初中, 10-12=高中 |
| `image_paths` | `repeated string` | 是 | 至少 1 条，合法 OSS 路径 | 图片路径列表 |
| `comments` | `string` | 否 | 最大长度不限，空时传 `""` | 备注/描述 |
| `source_upload_id` | `string` | 是 | 非空，GUID 格式 | 来源上传记录 ID，用于关联但不做唯一性校验 |

### 1.2 响应参数

| 字段 | 类型 | 含义 |
|------|------|------|
| `success` | `bool` | 是否创建成功 |
| `error_message` | `string` | 失败时的错误信息 |
| `created_item_ids` | `repeated string` | 成功创建的错题条目 ID 列表 |

### 1.3 超时配置

| 调用方 | 超时 |
|--------|------|
| `GrpcMistakeSubmitter` | 30 秒（`CancellationTokenSource.CancelAfter(30s)`） |
| `GrpcUploadDispatcher.DispatchToMistakeAsync` | 使用 gRPC 客户端默认超时 |

### 1.4 幂等性说明

**非幂等**。每次调用 `SubmitMistakeUpload` 都会创建新的错题条目。`source_upload_id` 仅作为关联字段存储，不做唯一性校验。相同 `source_upload_id` 重复调用会产生重复错题。

## 2. RemoveImagesFromRecord（mistake → student）

### 2.1 请求参数

| 字段 | 类型 | 必填 | 取值范围 | 业务含义 |
|------|------|------|----------|----------|
| `record_id` | `string` | 是 | 非空，GUID 格式 | 上传记录 ID（对应 mistake 侧的 `source_upload_id`） |
| `student_id` | `string` | 是 | 非空 GUID | 学生唯一标识 |
| `image_paths` | `repeated string` | 是 | 至少 1 条，合法 OSS 路径 | 需要从记录中移除的图片路径列表 |

### 2.2 响应参数

| 字段 | 类型 | 含义 |
|------|------|------|
| `success` | `bool` | 是否移除成功 |
| `error_message` | `string` | 失败时的错误信息 |
| `record_deleted` | `bool` | 记录是否因图片全部移除而被删除 |
| `remaining_image_count` | `int32` | 记录中剩余图片数量 |

### 2.3 超时配置

| 调用方 | 超时 |
|--------|------|
| `MistakeServiceImpl` | 使用 gRPC 客户端默认超时（无显式设置） |

### 2.4 幂等性说明

**幂等**。移除已不存在的图片路径不会报错，返回当前记录状态。重复调用相同请求结果一致。

## 3. 健康检查（student → mistake）

### 3.1 IsMistakeServiceAvailableAsync

| 项目 | 说明 |
|------|------|
| 方式 | HTTP GET 请求 mistake 服务 HTTP 端点 |
| 超时 | 3 秒 |
| 返回 | `true`（HTTP 2xx）/ `false`（异常或非 2xx） |
| 用途 | `GrpcUploadDispatcher` 判断 mistake 服务是否可用 |

## 4. 错误码映射

### 4.1 gRPC 状态码

| StatusCode | 场景 | 处理方式 |
|------------|------|----------|
| `UNAVAILABLE` | mistake/student 服务不可达 | GrpcUploadDispatcher 返回 `false`；MistakeServiceImpl 仅 log error |
| `DEADLINE_EXCEEDED` | 调用超时 | GrpcMistakeSubmitter 抛出异常；GrpcUploadDispatcher 捕获返回 `false` |
| `INVALID_ARGUMENT` | 参数非法（如空 studentId） | 上层根据 `success=false` 和 `error_message` 处理 |
| `INTERNAL` | 服务端内部错误 | GrpcUploadDispatcher 返回 `false`；MistakeServiceImpl 仅 log error |

### 4.2 业务错误

| 场景 | 返回 | 说明 |
|------|------|------|
| SubmitMistakeUpload 成功 | `success=true, created_item_ids=[...]` | 返回创建的条目 ID |
| SubmitMistakeUpload 失败 | `success=false, error_message="..."` | 业务校验不通过等 |
| RemoveImagesFromRecord 成功 | `success=true` | 图片已移除 |
| RemoveImagesFromRecord 失败 | `success=false, error_message="..."` | 记录不存在等 |

## 5. 配置项

| 配置键 | 所在服务 | 默认值 | 说明 |
|--------|----------|--------|------|
| `StudentGrpcService:Address` | ruoyu.mistake | `http://localhost:5005` | student 服务 gRPC 地址 |
| `MistakeGrpcService:Address` | ruoyu.student | — | mistake 服务 gRPC 地址（通过 AddGrpcClient 配置） |
| `MistakeHttpAddress` | ruoyu.student | — | mistake 服务 HTTP 健康检查地址 |
