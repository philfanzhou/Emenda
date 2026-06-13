# Mistake 与 Portals 集成 — 任务清单

## admin_portal 集成

### gRPC 客户端注册
- [ ] 配置 `MistakeGrpcService:Address` = `http://localhost:5006`
- [ ] 注册 `MistakeGrpcService.MistakeGrpcServiceClient` 到 DI
- [ ] 注册 `StudentManagementGrpcService.StudentManagementGrpcServiceClient` 到 DI

### MistakeController
- [ ] GET /api/admin/mistakes — 分页查询错题列表
- [ ] GET /api/admin/mistakes/{id} — 查询单条错题
- [ ] GET /api/admin/mistakes/by-upload/{uploadId} — 按上传记录查询
- [ ] PUT /api/admin/mistakes/{id} — 更新错题
- [ ] POST /api/admin/mistakes/migrate-images/{id} — 手动迁移图片

### 学生姓名补充
- [ ] 列表查询中批量获取学生姓名（去重 studentId 后逐个调用 GetStudentAsync）
- [ ] 单条查询中获取学生姓名
- [ ] GetStudentAsync 失败时降级为显示 studentId

### 图片迁移
- [ ] 筛选 uploads/ 前缀的图片路径
- [ ] 逐个执行 CopyObjectAsync + DeleteAsync
- [ ] pathMapping 缓存避免重复迁移
- [ ] 迁移后调用 UpdateMistakeItem 更新路径
- [ ] 单个图片迁移失败时中断并返回错误

### OssAuditWorker
- [ ] 调用 GetAllReferencedImagePaths 获取错题引用路径
- [ ] 与 Student 引用路径交叉比对
- [ ] 识别僵尸对象

## teacher_portal 集成

### gRPC 客户端注册
- [ ] 配置 `MistakeService:GrpcUrl` = `http://localhost:5006`
- [ ] 注册 `MistakeGrpcService.MistakeGrpcServiceClient` 到 DI
- [ ] 注册 `TeacherDbService` 到 DI

### MistakeController
- [ ] GET /api/mistake/pending-reviews — 待审核上传记录列表
- [ ] GET /api/mistake/review-detail/{sourceUploadId} — 审核详情
- [ ] POST /api/mistake/review-item — 审核单条
- [ ] POST /api/mistake/batch-review — 批量审核
- [ ] GET /api/mistake/{id} — 错题详情
- [ ] POST /api/mistake/{id}/return — 退回错题
- [ ] POST /api/mistake/add-item — 手动添加
- [ ] POST /api/mistake/complete-review — 完成审核
- [ ] GET /api/mistake/list — 错题列表
- [ ] GET /api/mistake/pending-items — 待审核条目

### 权限过滤
- [ ] 实现 GetTeacherSubjects() 方法（从 JWT 获取 userId → TeacherDbService.GetSubjectsByUserId）
- [ ] pending-reviews 接口学科过滤
- [ ] pending-items 接口学科过滤
- [ ] list 接口学科过滤（内存过滤 + 分页）

### 驳回后清理
- [ ] 单条审核驳回 → DeleteMistakeItem → 检查剩余 → MarkUploadRecordAsReturned
- [ ] 批量审核驳回 → 同上逻辑
- [ ] 退回错题 → DeleteMistakeItem → MarkUploadRecordAsReturned

### 确认后自动完成审核
- [ ] 单条审核确认 → 检查是否全部审核完成 → CompleteUploadReview → DeleteUploadRecordAfterReview
- [ ] 批量审核确认 → 收集 sourceUploadIds → 统一检查每个上传记录 → CompleteUploadReview → DeleteUploadRecordAfterReview
- [ ] 手动完成审核 → CompleteUploadReview → DeleteUploadRecordAfterReview

## user_portal 集成

### gRPC 客户端注册
- [ ] 配置 `MistakeService:GrpcUrl` = `http://localhost:5006`
- [ ] 注册 `MistakeGrpcService.MistakeGrpcServiceClient` 到 DI

### MistakeController
- [ ] GET /api/mistake/list — 学生错题列表（ReviewStatus=Confirmed）
- [ ] GET /api/mistake/{id} — 错题详情
- [ ] DELETE /api/mistake/{id} — 删除错题（仅非 Confirmed）

### 权限控制
- [ ] studentId 必填校验
- [ ] ReviewStatus 固定为 Confirmed
- [ ] 删除时校验状态（Confirmed 不允许删除）

## 通用

### 错误处理
- [ ] gRPC RpcException 映射为 HTTP 状态码
- [ ] 统一错误响应格式
- [ ] 日志记录规范

### 配置
- [ ] 各 Portal 的 gRPC 地址配置
- [ ] 分页参数默认值与上限配置
