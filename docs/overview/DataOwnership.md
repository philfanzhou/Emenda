# 数据归属 — ruoyu.mistake

## 本服务拥有的数据实体（主责）

mistake 服务对以下表拥有完整的 CRUD 权限和生命周期管理责任。

### mistake_item

- 22 字段，主键 UUID
- JSONB 字段: `source_regions`（图片裁剪区域信息）
- 5 个索引
- 审核状态流转: PendingReview → Confirmed / Rejected
- **mistake 是此表的唯一写入者**

### mistake_practice

- 8 字段，主键 UUID
- JSONB 字段: `image_paths`（练习图片路径）
- FK → mistake_item (CASCADE DELETE)
- 3 个索引
- 仅允许为 Confirmed 状态的错题添加练习记录

### process_log

- 5 字段，主键 UUID
- FK → mistake_item (CASCADE DELETE)
- 2 个索引
- 记录错题全生命周期操作: Created → Analyzed → ReviewConfirmed/ReviewRejected → Practiced → Mastered

## 本服务引用的外部数据（只读引用，不双写）

| 引用字段 | 归属服务 | 归属表/资源 | mistake 侧行为 |
|----------|----------|-------------|----------------|
| `student_id` | ruoyu.student | 学生档案 | 仅存储引用，不校验学生是否存在 |
| `source_upload_id` | ruoyu.student | image_upload_record.id | 仅存储引用，无外键约束；审核完成后回调 student 清理 |
| `source_regions[].sourceImagePath` | OSS | uploads/ 或 mistakes/ 前缀 | 拥有路径引用，审核完成后执行迁移 |
| `reviewer_id` | Identity (认证系统) | 用户表 | 仅存储引用，不校验审核人身份；由 portal 层从 JWT 提取 |

## 禁止直接写入的外部表

| 禁止写入的表 | 归属服务 | 原因 |
|-------------|----------|------|
| image_upload_record | ruoyu.student | student 服务的数据权威，mistake 仅通过 gRPC 回调间接影响 |
| student 相关表 | ruoyu.student | 跨服务数据隔离，仅通过 API 交互 |
| Quaestura（原 questionbank）相关表 | 外部仓库 Quaestura | 物理隔离的独立数据库，无直接访问 |
| homework 相关表 | ruoyu.homework | 无直接集成，mistake 与 homework 之间无 gRPC 调用 |

## OSS 路径归属

| 路径前缀 | 主责服务 | 说明 |
|----------|----------|------|
| `uploads/` | ruoyu.student | student 服务主责，mistake 只引用 |
| `mistakes/` | ruoyu.mistake | mistake 服务主责，审核完成后图片迁移到此 |

**迁移规则**: `uploads/{path}` → `mistakes/{path}`，仅前缀变更，路径结构保持一致。
