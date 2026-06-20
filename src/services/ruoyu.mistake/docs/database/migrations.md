# 迁移历史

## 20260514051544_InitialCreate

**日期**: 2026-05-14
**类型**: 初始创建

### 创建的表

1. **mistake_item** — 包含所有字段及 6 个索引
2. **mistake_practice** — 包含外键 CASCADE 约束及 4 个索引
3. **process_log** — 包含外键 CASCADE 约束及 3 个索引

### 旧表迁移（DatabaseInitializer）

`DatabaseInitializer.MigrateOldSchemaAsync` 处理从旧表 `mistake` 到 `mistake_item` 的迁移：

1. 检测旧表 `mistake` 是否存在
2. 删除旧表的外键约束
3. 如果 `mistake_item` 已存在：直接删除旧表
4. 如果 `mistake_item` 不存在：重命名表 + 添加新列 + 移除旧列 + 创建新索引
5. 修复 `process_log` 和 `mistake_practice` 的列名 `mistake_id` → `mistake_item_id` 及外键约束

### 列重命名

| 旧列名 | 新列名 | 所在表 |
|--------|--------|--------|
| `status` | `review_status` | mistake_item |
| `mistake_id` | `mistake_item_id` | process_log, mistake_practice |

### 新增列

| 列名 | 类型 | 所在表 |
|------|------|--------|
| `source_upload_id` | `VARCHAR(64)` | mistake_item |
| `source_regions` | `JSONB` | mistake_item |
| `reviewer_id` | `VARCHAR(36)` | mistake_item |
| `reviewed_at` | `TIMESTAMPTZ` | mistake_item |
| `review_comment` | `TEXT` | mistake_item |
| `type` | `INT` | mistake_item |
| `question_id` | `VARCHAR(36)` | mistake_item |

### 已移除的列（旧表）

| 列名 | 说明 |
|------|------|
| `date` | 旧版本日期字段，已被 `created_at` 替代 |
| `image_paths` | 旧版本图片路径，已被 `source_regions` (JSONB) 替代 |
| `fingerprint` | 图片指纹字段，已移除 |

## 20260612030015_AddStudentIdToProcessLog

**日期**: 2026-06-12
**类型**: 增量迁移

### 变更内容

1. **process_log** — 新增 `student_id` 列 (`VARCHAR(36)`, NULL) 及索引 `IX_process_log_student_id`
2. **mistake_practice** — `image_paths` 列类型从 `text` 改为 `jsonb`

### 幂等性问题与 stamp 机制

此迁移存在与 `EnsureColumnRenamesAsync` 的竞态问题：

- `DatabaseInitializer.EnsureColumnRenamesAsync` 在启动时通过 `IF NOT EXISTS` 幂等地添加 `student_id` 列
- 但 `StampMigrationsIfTablesExistAsync` 只标记了 `InitialCreate`，未标记此迁移
- 导致 `MigrateAsync` 认为此迁移未应用，尝试执行 `ALTER TABLE ADD COLUMN student_id` → 列已存在 → 42701 错误

**修复方案**：在 `StampMigrationsIfTablesExistAsync` 中增加对此迁移的检测：当 `process_log` 表已存在 `student_id` 列时，自动将 `20260612030015_AddStudentIdToProcessLog` 标记为已应用。

### 新增列

| 列名 | 类型 | 所在表 |
|------|------|--------|
| `student_id` | `VARCHAR(36)` NULL | process_log |

### 类型变更

| 列名 | 旧类型 | 新类型 | 所在表 |
|------|--------|--------|--------|
| `image_paths` | `text` | `jsonb` | mistake_practice |

## 20260620_RenameReviewCommentToRootCause

**日期**: 2026-06-20
**类型**: 列重命名

### 变更内容

`mistake_item` 表：`review_comment` 列重命名为 `root_cause`

### 背景

原 `review_comment` 字段用于老师审核意见，但实际使用率极低。改为 `root_cause` 后，由 VL 分析在创建错题时自动生成错误根因，老师审核时可修改。纯文本格式，不做结构化分类。

### 列重命名

| 旧列名 | 新列名 | 所在表 |
|--------|--------|--------|
| `review_comment` | `root_cause` | mistake_item |

### DatabaseInitializer 处理

在 `EnsureColumnRenamesAsync` 中增加：当 `mistake_item` 表存在 `review_comment` 列且不存在 `root_cause` 列时，执行 `ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause`。

## 相关文档

- [表结构文档](./tables/)
- [实体关系](./relations.md)
- [总览](./README.md)
