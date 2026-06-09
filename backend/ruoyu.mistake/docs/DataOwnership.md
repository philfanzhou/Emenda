# 数据归属文档 — ruoyu.mistake

本文档明确 ruoyu.mistake 服务中各类数据的归属关系，区分主责数据与引用数据，并描述跨服务引用的语义、约束及 OSS 路径归属规则。

---

## 一、主责数据（mistake 拥有完整生命周期）

mistake 服务对以下表拥有完整的 CRUD 权限，负责其创建、读取、更新、删除及生命周期管理。

### 1.1 mistake_item 表

| 字段 | 类型 | 说明 |
|---|---|---|
| id | UUID (PK) | 主键 |
| student_id | varchar(36) | 引用字段，指向 student 服务的学生 |
| subject | int | 科目，取值 1–9 |
| grade | int | 年级，取值 1–12 |
| source_upload_id | varchar(64) | 引用字段，指向 student 服务的 image_upload_record.id |
| source_regions | JSONB | 图片区域信息 |
| review_status | int | 审核状态 |
| reviewer_id | varchar(36) | 引用字段，指向认证系统中的审核人 |
| reviewed_at | timestamp | 审核时间 |
| review_comment | text | 审核备注 |
| type | int | 错题类型 |
| question_id | varchar(36) | 关联题目 ID |
| created_at | timestamp | 创建时间 |
| updated_at | timestamp | 更新时间 |

**归属说明：**

- mistake 拥有 `mistake_item` 的完整 CRUD 权限。
- `student_id` 为引用字段，指向 student 服务的学生，但 mistake **不校验**学生是否存在。
- `source_upload_id` 为引用字段，指向 student 服务的 `image_upload_record.id`，跨服务关联**无外键约束**。

### 1.2 mistake_practice 表

| 字段 | 类型 | 说明 |
|---|---|---|
| id | UUID (PK) | 主键 |
| mistake_item_id | UUID (FK CASCADE) | 关联 mistake_item.id，级联删除 |
| student_id | varchar(36) | 引用字段，指向 student 服务的学生 |
| practice_date | date | 练习日期 |
| image_paths | JSONB | 练习图片路径 |
| student_answer | text | 学生作答 |
| is_correct | bool | 是否正确 |
| comments | text | 备注 |

**级联删除规则：** 删除 `mistake_item` 时，自动删除关联的 `mistake_practice` 记录。

### 1.3 process_log 表

| 字段 | 类型 | 说明 |
|---|---|---|
| id | UUID (PK) | 主键 |
| mistake_item_id | UUID (FK CASCADE) | 关联 mistake_item.id，级联删除 |
| action | int | 操作类型 |
| details | text | 操作详情 |
| created_at | timestamp | 创建时间 |

**级联删除规则：** 删除 `mistake_item` 时，自动删除关联的 `process_log` 记录。

---

## 二、引用数据（mistake 只引用，不拥有）

mistake 服务存储以下外部数据的引用 ID，但不拥有这些数据的生命周期，也不校验引用对象是否存在。

### 2.1 student_id

| 属性 | 值 |
|---|---|
| 归属服务 | ruoyu.student |
| mistake 侧行为 | 仅存储引用，不校验学生是否存在 |
| 展示时行为 | portal 层（admin_portal, teacher_portal）调用 student 服务获取学生姓名 |

### 2.2 source_upload_id

| 属性 | 值 |
|---|---|
| 归属服务 | ruoyu.student（image_upload_record.id） |
| mistake 侧行为 | 通过此字段关联上传记录，无外键约束 |
| 回调行为 | `CompleteUploadReview` 完成后，回调 student 服务的 `RemoveImagesFromRecord` 清理图片 |

### 2.3 source_regions[].sourceImagePath

| 属性 | 值 |
|---|---|
| 归属 | OSS 对象存储 |
| 初始值 | `uploads/` 前缀（来自 student 上传） |
| 审核完成后 | 迁移为 `mistakes/` 前缀 |
| mistake 侧行为 | 拥有路径引用，但不拥有 OSS 对象的完整生命周期（student 也管理 `uploads/` 下的对象） |

### 2.4 reviewer_id

| 属性 | 值 |
|---|---|
| 归属 | 认证系统（Identity） |
| mistake 侧行为 | 仅存储引用，不校验审核人身份 |
| 来源 | portal 层负责从 JWT token 中提取 reviewer_id |

---

## 三、数据库归属

| 数据库 | 归属服务 | 说明 |
|---|---|---|
| ruoyu_study_mistake | ruoyu.mistake | mistake 服务专属数据库 |
| ruoyu_study_questionbank | ruoyu.questionbank | 物理分离 |
| ruoyu_study_student | ruoyu.student | 物理分离 |

三个数据库**物理隔离**，跨服务访问通过 API 调用实现，不通过跨库查询。

---

## 四、OSS 路径归属

| 路径前缀 | 主责服务 | 说明 |
|---|---|---|
| `uploads/` | ruoyu.student | student 服务主责，mistake 只引用 |
| `mistakes/` | ruoyu.mistake | mistake 服务主责，审核完成后图片迁移到此 |

### 迁移规则

- **迁移语义：** `uploads/{path}` → `mistakes/{path}`
- **路径结构：** 迁移前后保持一致，仅前缀变更
- **触发时机：** 审核完成时执行迁移
- **迁移后清理：** 迁移完成后，通过回调 student 服务的 `RemoveImagesFromRecord` 清理原上传记录中的图片引用
