# Mistake 与 Portals 集成 — 功能概述

## 概述

Mistake 模块通过 gRPC 为三个 Portal 提供错题业务接口。Mistake 服务本身不做权限校验，权限控制在各 Portal 层完成。各 Portal 根据自身角色（管理员/教师/学生）调用不同的 gRPC 方法，并在 Portal 层补充额外业务逻辑。

## 三个 Portal 的集成角色

### admin_portal — 管理查看与运维

- **代码位置**：`admin_portal\backend\Controllers\MistakeController.cs`
- **gRPC 配置**：`MistakeGrpcService:Address` = `http://localhost:5006`
- **核心职责**：
  - 分页查询错题列表（支持按学生/学科/年级/审核状态筛选）
  - 查询单条错题详情
  - 按上传记录查询错题
  - 更新错题信息（学生ID/学科/年级）
  - 手动迁移图片（uploads/ → mistakes/）
- **额外行为**：
  - 查询结果中补充学生姓名（调用 Student 的 `GetStudentAsync`）
  - `OssAuditWorker` 调用 `GetAllReferencedImagePaths` 进行 OSS 僵尸对象审计
- **权限**：无特殊权限控制，管理员可查看所有数据

### teacher_portal — 审核流程核心

- **代码位置**：`teacher_portal\backend\Controllers\MistakeController.cs`
- **gRPC 配置**：`MistakeService:GrpcUrl` = `http://localhost:5006`
- **核心职责**：
  - 获取待审核上传记录列表
  - 获取审核详情（按上传记录）
  - 单条审核（确认/驳回）
  - 批量审核
  - 手动添加错题
  - 完成审核
  - 退回错题
  - 查询错题列表与详情
  - 查询待审核条目
- **额外行为**：
  - **驳回后清理**：调用 `DeleteMistakeItem` 删除错题 → 检查上传记录下是否还有剩余错题 → 若无则调用 Student 的 `MarkUploadRecordAsReturned` 标记上传记录为已退回
  - **确认后自动完成审核**：检查上传记录下是否还有待审核条目 → 若无则调用 `CompleteUploadReview` → 调用 Student 的 `DeleteUploadRecordAfterReviewAsync` 删除上传记录
- **权限**：通过 `TeacherDbService.GetSubjectsByUserId` 过滤教师可查看的学科

### user_portal — 学生查看

- **代码位置**：`user_portal\backend\Controllers\MistakeController.cs`
- **gRPC 配置**：`MistakeService:GrpcUrl` = `http://localhost:5006`
- **核心职责**：
  - 查看自己的错题列表
  - 查看错题详情
  - 删除错题（仅非 Confirmed 状态）
- **额外行为**：
  - 只查询 `Confirmed` 状态的错题
  - 非 Confirmed 状态不允许删除
- **权限**：通过 `studentId` 参数过滤（前端传入），学生只能查看自己的错题

## 权限模型

| Portal | 权限控制方式 | 说明 |
|--------|-------------|------|
| admin_portal | 无特殊控制 | 管理员可查看所有数据 |
| teacher_portal | 学科过滤 | 通过 `TeacherDbService.GetSubjectsByUserId` 获取教师负责的学科，仅返回该学科下的数据 |
| user_portal | 学生ID过滤 | 前端传入 `studentId`，仅查询该学生的错题 |

认证由各 Portal 层通过 Identity 服务完成，Mistake 服务本身不做权限校验。
