# 错题本 gRPC 服务 - 设计文档

## 1. 架构设计

### 1.1 分层架构

本解决方案采用清晰的分层架构，包含五个项目：

```
+---------------------------------------------------+
|  Host (Ruoyu.Study.Mistake.Host)                  |
|  - ASP.NET Core gRPC 服务器                        |
|  - 依赖注入配置                                     |
|  - 健康检查                                         |
+---------------------------------------------------+
|  Service (Ruoyu.Study.Mistake.Service)            |
|  - gRPC 服务实现 (MistakeServiceImpl)              |
|  - 请求/响应映射                                    |
|  - 输入验证 + 跨服务调用协调                         |
+---------------------------------------------------+
|  Domain (Ruoyu.Study.Mistake.Domain)              |
|  - 领域模型 (MistakeItemModel, etc.)               |
|  - 领域服务 (MistakeDomainService)                  |
|  - 仓储接口                                         |
+---------------------------------------------------+
|  Database (Ruoyu.Study.Mistake.Database)          |
|  - Entity Framework Core DbContext                 |
|  - 实体配置 + 迁移                                  |
|  - 仓储实现 + 工作单元                               |
+---------------------------------------------------+
|  Contract (Ruoyu.Study.Mistake.Contract)          |
|  - Protocol Buffer 定义 (mistake.proto)            |
|  - 共享 DTO 和枚举                                  |
+---------------------------------------------------+
```

### 1.2 项目依赖关系

```
Host --> Service --> Domain --> Contract
  |       |          |
  |       |          v
  |       +----> Database --> Domain --> Contract
  |
  +------------> Database --> Domain --> Contract
```

### 1.3 职责划分

| 项目 | 职责 |
|------|------|
| Contract | 定义 gRPC 契约、消息类型和枚举（ReviewStatus, ProcessAction, SourceRegion 等）|
| Domain | 定义领域模型（MistakeItemModel, MistakePracticeModel, ProcessLogModel）、业务逻辑和仓储接口 |
| Database | 实现仓储接口，提供 EF Core + PostgreSQL 的数据访问，含 Migration 管理 |
| Service | 实现 gRPC 服务，处理请求映射、验证，协调领域服务与外部依赖（OSS、Student 服务）|
| Host | 应用程序入口，配置依赖注入、中间件和启动服务 |

---

## 2. 数据模型设计

### 2.1 错题条目 (MistakeItem)

| 字段 | 类型 | 约束 | 描述 |
|------|------|------|------|
| Id | GUID | 主键 | 唯一标识符 |
| StudentId | string | 最大 36 字符, 必填 | 学生引用 |
| Subject | int | 必填 | 学科编号 (1-9) |
| Grade | int | 必填 | 年级编号 (1-12) |
| SourceUploadId | string | 最大 64 字符, 必填 | 来源上传记录 ID |
| SourceRegions | JSONB | | 图片区域引用，JSON 数组 |
| ReviewStatus | int | 默认 1 | 审核状态：1=PendingReview, 2=Confirmed, 3=Rejected |
| ReviewerId | string | 最大 36 字符 | 审核人 ID |
| ReviewedAt | DateTimeOffset? | | 审核时间 |
| ReviewComment | string | | 审核意见 |
| Type | int | 默认 0 | 错题类型 |
| QuestionId | string | 最大 36 字符 | 关联题库 ID |
| CreatedAt | DateTimeOffset | 必填 | 创建时间 |
| UpdatedAt | DateTimeOffset? | | 最后更新时间 |

### 2.2 错题练习记录 (MistakePractice)

| 字段 | 类型 | 约束 | 描述 |
|------|------|------|------|
| Id | GUID | 主键 | 唯一标识符 |
| MistakeItemId | GUID | 外键, 级联删除 | 关联的错题条目 |
| StudentId | string | 最大 36 字符, 必填 | 学生引用 |
| PracticeDate | DateTimeOffset | 必填 | 练习日期 |
| ImagePaths | JSONB | | 练习上传的图片路径 |
| StudentAnswer | string | | 学生答案 |
| IsCorrect | bool | 必填 | 是否正确 |
| Comments | string | | 备注 |

### 2.3 处理日志 (ProcessLog)

| 字段 | 类型 | 约束 | 描述 |
|------|------|------|------|
| Id | GUID | 主键 | 唯一标识符 |
| MistakeItemId | GUID | 外键, 级联删除 | 关联的错题条目 |
| Action | int | 必填 | 操作类型枚举 |
| Details | string | | 操作详情 |
| CreatedAt | DateTimeOffset | 必填 | 创建时间 |

### 2.4 实体关系

- 一个学生可以有多条错题条目（一对多）
- 一条上传记录可对应 0..N 条错题条目（通过 source_upload_id，跨服务关联）
- 一条错题条目可以有多条练习记录（一对多，级联删除）
- 一条错题条目可以有多条处理日志（一对多）
- source_regions 中的一组图片 = 一道错题

### 2.5 审核状态流转

```
PendingReview ──→ Confirmed
     │
     └──────────→ Rejected
```

---

## 3. gRPC 服务契约设计

### 3.1 服务定义

```protobuf
service MistakeGrpcService {
  // 错题分发
  rpc SubmitMistakeUpload(SubmitMistakeUploadRequest) returns (SubmitMistakeUploadResponse);

  // 错题条目管理
  rpc GetMistakeItem(IdRequest) returns (MistakeItemDto);
  rpc GetMistakeItemsByUpload(GetMistakeItemsByUploadRequest) returns (MistakeItemListResponse);
  rpc GetMistakeItemList(GetMistakeItemListRequest) returns (MistakeItemPageResult);
  rpc AddMistakeItem(AddMistakeItemRequest) returns (MistakeItemDto);
  rpc UpdateMistakeItem(UpdateMistakeItemRequest) returns (MistakeItemDto);
  rpc DeleteMistakeItem(IdRequest) returns (BoolResponse);

  // 错题审核
  rpc ReviewMistakeItem(ReviewMistakeItemRequest) returns (BoolResponse);
  rpc GetPendingReviewUploads(GetPendingReviewUploadsRequest) returns (PendingReviewUploadListResponse);
  rpc CompleteUploadReview(CompleteUploadReviewRequest) returns (CompleteUploadReviewResponse);

  // 错题练习
  rpc AddMistakePractice(AddMistakePracticeRequest) returns (MistakePracticeDto);
  rpc GetMistakePractices(IdRequest) returns (MistakePracticeListResponse);

  // 错题分析
  rpc AnalyzeMistakeType(AnalyzeMistakeRequest) returns (MistakeTypeResponse);

  // 处理日志
  rpc LogMistakeProcess(LogMistakeProcessRequest) returns (BoolResponse);
  rpc GetMistakeProcessHistory(IdRequest) returns (ProcessLogListResponse);

  // 图片路径查询
  rpc GetAllReferencedImagePaths(Empty) returns (GetAllReferencedImagePathsResponse);
}
```

### 3.2 枚举定义

```protobuf
enum ReviewStatus {
  REVIEW_STATUS_UNSPECIFIED = 0;
  REVIEW_STATUS_PENDING_REVIEW = 1;
  REVIEW_STATUS_CONFIRMED = 2;
  REVIEW_STATUS_REJECTED = 3;
}

enum ProcessAction {
  PROCESS_ACTION_UNSPECIFIED = 0;
  PROCESS_ACTION_CREATED = 1;
  PROCESS_ACTION_ANALYZED = 2;
  PROCESS_ACTION_REVIEW_CONFIRMED = 3;
  PROCESS_ACTION_REVIEW_REJECTED = 4;
  PROCESS_ACTION_PRACTICED = 5;
  PROCESS_ACTION_MASTERED = 6;
}
```

### 3.3 核心消息结构

```protobuf
message MistakeItemDto {
  string id = 1;
  string student_id = 2;
  int32 subject = 3;
  int32 grade = 4;
  string source_upload_id = 5;
  repeated SourceRegion source_regions = 6;
  ReviewStatus review_status = 7;
  string reviewer_id = 8;
  string reviewed_at = 9;
  string review_comment = 10;
  int32 type = 11;
  string question_id = 12;
  string created_at = 13;
  string updated_at = 14;
}

message SourceRegion {
  string source_image_path = 1;
  BoundingBox bounding_box = 2;
}

message BoundingBox {
  int32 x1 = 1;
  int32 y1 = 2;
  int32 x2 = 3;
  int32 y2 = 4;
}
```

---

## 4. 关键流程设计

### 4.1 错题分发流程 (SubmitMistakeUpload)

```
请求进入 gRPC 层
  ├── 参数校验（学生ID、学科1-9、年级1-12、图片非空）
  ├── MistakeDomainService.SubmitMistakeUploadAsync
  │     ├── ValidateSubmitData（聚合校验）
  │     ├── 构建 MistakeItemModel (ReviewStatus = PendingReview)
  │     ├── _mistakeItemRepository.AddAsync(item)
  │     ├── AddProcessLogAsync (Action = Created)
  │     └── _unitOfWork.SaveChangesAsync()  -- 事务提交
  └── 返回 created_item_ids[]
```

### 4.2 审核完成流程 (CompleteUploadReview)

```
MistakeDomainService.CompleteUploadReviewAsync
  ├── 校验：source_upload_id 下不存在 PendingReview 状态的 item
  └── 返回所有 item 列表

MistakeServiceImpl.CompleteUploadReview (gRPC 层)
  ├── 遍历 items：
  │     ├── Confirmed item：
  │     │     ├── OSS copy uploads/{path} → mistakes/{path}
  │     │     ├── OSS delete uploads/{path}
  │     │     └── 更新 source_regions 中的路径
  │     └── Rejected item：不迁移图片
  ├── 跨服务调用 Student.RemoveImagesFromRecord(recordId, studentId, imagePaths)
  │     └── Student 服务移除 image_upload_record 中的图片，全空则删除记录
  └── 返回成功
```

### 4.3 删除错题流程 (DeleteMistakeItem)

```
MistakeDomainService.DeleteMistakeItemAsync
  ├── 查找 item（不存在则返回 false）
  ├── _mistakePracticeRepository.RemoveByMistakeItemIdAsync(id)  -- 级联清理练习
  ├── _processLogRepository.RemoveByMistakeItemIdAsync(id)       -- 级联清理日志
  ├── _mistakeItemRepository.DeleteAsync(id)                      -- 删除错题
  └── _unitOfWork.SaveChangesAsync()  -- 事务提交
```

---

## 5. 技术栈

| 组件 | 技术 | 说明 |
|------|------|------|
| 框架 | .NET 8.0 | ASP.NET Core gRPC |
| 通信 | gRPC | protobuf 契约定义 |
| ORM | Entity Framework Core 8.0 | Npgsql provider |
| 数据库 | PostgreSQL | 单库 ruoyu_study_mistake |
| 对象存储 | MinIO / SeaweedFS | S3 兼容接口 |
| 测试 | xUnit + Moq + FluentAssertions | 单元测试 + Mock |

---

## 6. 测试设计

### 6.1 领域服务测试覆盖

- **MistakeDomainService**
  - 使用有效数据提交错题分发
  - 参数校验（空学生ID、无效学科、无效年级、空图片列表）
  - 按 ID 查询错题（存在 / 不存在）
  - 按 source_upload_id 查询错题列表
  - 分页查询错题列表
  - 审核错题（正常流程 / 非待审核状态拦截）
  - 完成上传审核（有待审核项时拒绝）
  - 添加练习记录（已确认错题 / 非确认状态拦截）
  - 删除错题条目（存在 / 不存在）
  - 错题类型分析
  - 处理日志记录与查询

### 6.2 测试基础设施

- xUnit 作为测试框架
- Moq 用于仓储和工作单元模拟
- 测试覆盖关键业务逻辑和参数校验路径

---

## 7. 部署与运维设计

### 7.1 健康检查

服务通过 ASP.NET Core 健康检查中间件暴露健康检查端点。

### 7.2 配置

- 数据库连接字符串：`ConnectionStrings:Default`
- OSS 配置：通过 `IOssService` 接口注入（支持 MinIO / SeaweedFS / LocalFile）
- Student 服务地址：gRPC 客户端配置

### 7.3 对象存储路径设计

```
mistakes/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/original_{index}.jpg
mistakes/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/practice_{practice_id}_{index}.jpg
```

| 维度 | 说明 |
|------|------|
| subject | 科目维度，便于按科目查询和管理 |
| student_id | 学生维度，便于按学生查询和清理 |
| year/month/day | 时间维度，便于按时间归档 |
| mistake_id | 错题维度，便于整题清理 |
| original_ / practice_ | 类型前缀，区分原图和练习图 |

---

## 8. 文档历史

| 版本 | 日期 | 作者 | 变更 |
|------|------|------|------|
| 1.0 | 2026-06-09 | 架构团队 | 初始设计文档，基于代码分析重构 |