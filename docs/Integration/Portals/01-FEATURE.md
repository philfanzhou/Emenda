# Mistake 与 Portals 集成 — 功能概述

> **消费方迁出说明（2026-09-24）**：本文中的 `Ruoyu.Admin`（原 `admin_portal` / Admin Portal）已于 [ADR-0010](../../../../../../docs/adr/0010-extract-ruoyu-admin.md) 迁出为独立开源仓库 [Ruoyu.Admin](https://github.com/philfanzhou/Ruoyu.Admin)。**本文件描述的端点契约仍由本仓库该服务主责**，消费方改为外部仓库；契约变更不会在该仓库产生编译错误（它持有手写镜像 DTO），因此变更时必须把 Ruoyu.Admin 视为受影响方并同步通知。 **停用说明（2026-11-07）**：Ruoyu.Admin 已按 [ADR-0030](../../../../../../docs/adr/0030-portal-hosted-administration.md) 停用。本文中由它发起的管理调用改由 Portal 管理端发出（User Portal 管理端：学生档案、Account Mapping、开放学科、上传记录；Teacher Portal 管理端：错题查看与修改；Assistant Portal 管理端：助教），其余调用（如存储审计引用快照）已无调用方。
>
> **协议描述已过期**：本文中以 gRPC 描述的交互在实际实现中已改为 HTTP/JSON，gRPC 已整体移除。这是迁出前就已存在的文档漂移，尚未逐条重新核对；当前实现事实以本服务的 `Controllers/` 与路由配置为准。


## 概述

Mistake 模块通过 HTTP 为三个 Portal 提供错题业务接口。Mistake 服务本身不做权限校验，权限控制在各 Portal 层完成。各 Portal 根据自身角色（管理员/教师/学生）调用不同的 HTTP 接口，并在 Portal 层补充额外业务逻辑。

## 三个 Portal 的集成角色

### Ruoyu.Admin — 管理查看与运维

- **代码位置**：`Ruoyu.Admin\backend\Controllers\MistakeController.cs`
- **HTTP 配置**：`MistakeService:Url` = `http://localhost:5007`
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
- **HTTP 配置**：`MistakeService:Url` = `http://localhost:5007`
- **核心职责**：
  - 获取待审核上传记录列表
  - 获取审核详情（按上传记录）
  - 单条审核（确认/退回）
  - 批量审核
  - 手动添加错题
  - 完成审核
  - 退回错题
  - 查询错题列表与详情
  - 查询待审核条目
- **额外行为**：
  - **退回后清理**：调用 `DeleteMistakeItem` 删除错题 → 检查上传记录下是否还有剩余错题 → 若无则调用 Student 的 `MarkUploadRecordAsReturned` 标记上传记录为已退回
  - **确认后自动完成审核**：检查上传记录下是否还有待审核条目 → 若无则调用 `CompleteUploadReview` → 调用 Student 的 `DeleteUploadRecordAfterReviewAsync` 删除上传记录
- **权限**：通过 `TeacherDbService.GetSubjectsByUserId` 过滤教师可查看的学科

### user_portal — 学生查看

- **代码位置**：`user_portal\backend\Controllers\MistakeController.cs`
- **HTTP 配置**：`MistakeService:Url` = `http://localhost:5007`
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
| Ruoyu.Admin | 无特殊控制 | 管理员可查看所有数据 |
| teacher_portal | 学科过滤 | 通过 `TeacherDbService.GetSubjectsByUserId` 获取教师负责的学科，仅返回该学科下的数据 |
| user_portal | 学生ID过滤 | 前端传入 `studentId`，仅查询该学生的错题 |

认证由各 Portal 层通过 Identity 服务完成，Mistake 服务本身不做权限校验。
