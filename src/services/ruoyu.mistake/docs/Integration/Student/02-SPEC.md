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

## 2. student_id 引用规则（mistake 侧）

mistake 不调用 student 的任何 gRPC 接口，仅通过 `student_id` 字段引用学生数据。

### 2.1 引用字段

| 表 | 字段 | 类型 | 约束 | 说明 |
|----|------|------|------|------|
| `mistake_item` | `student_id` | `VARCHAR(36)` | NOT NULL | 学生 ID，由 SubmitMistakeUpload 调用时传入 |
| `mistake_practice` | `student_id` | `VARCHAR(36)` | NOT NULL | 学生 ID，由 AddMistakePractice 调用时传入 |

### 2.2 引用语义

| 规则 | 说明 |
|------|------|
| 只读引用 | mistake 仅存储 `student_id`，不校验对应学生是否存在 |
| 无外键约束 | `student_id` 不设外键约束，mistake 不依赖 student 数据库 |
| 无 gRPC 调用 | mistake 不注册任何 student gRPC 客户端，不调用 student 的任何方法 |
| 数据来源 | `student_id` 由 student 服务在调用 `SubmitMistakeUpload` 时传入 |

### 2.3 索引

| 索引名 | 字段 | 类型 | 用途 |
|--------|------|------|------|
| `IX_mistake_item_student_id` | `student_id` | 普通 | 按学生查询错题 |
| `IX_mistake_item_student_id_subject_grade_review_status` | `student_id, subject, grade, review_status` | 复合 | 复合条件查询 |
| `IX_mistake_practice_student_id` | `student_id` | 普通 | 按学生查询练习记录 |

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
| `UNAVAILABLE` | mistake 服务不可达 | GrpcUploadDispatcher 返回 `false` |
| `DEADLINE_EXCEEDED` | 调用超时 | GrpcMistakeSubmitter 抛出异常；GrpcUploadDispatcher 捕获返回 `false` |
| `INVALID_ARGUMENT` | 参数非法（如空 studentId） | 上层根据 `success=false` 和 `error_message` 处理 |
| `INTERNAL` | 服务端内部错误 | GrpcUploadDispatcher 返回 `false` |

### 4.2 业务错误

| 场景 | 返回 | 说明 |
|------|------|------|
| SubmitMistakeUpload 成功 | `success=true, created_item_ids=[...]` | 返回创建的条目 ID |
| SubmitMistakeUpload 失败 | `success=false, error_message="..."` | 业务校验不通过等 |

## 5. 配置项

| 配置键 | 所在服务 | 默认值 | 说明 |
|--------|----------|--------|------|
| `MistakeGrpcService:Address` | ruoyu.student | — | mistake 服务 gRPC 地址（通过 AddGrpcClient 配置） |
| `MistakeHttpAddress` | ruoyu.student | — | mistake 服务 HTTP 健康检查地址 |
