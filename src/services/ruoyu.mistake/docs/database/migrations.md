# 迁移历史

## 20260514051544_InitialCreate

**日期**: 2026-05-14
**类型**: 初始创建

### 创建的表

1. **mistake_item** — 包含所有字段及 6 个索引
2. **mistake_practice** — 包含外键 CASCADE 约束及 4 个索引
3. **process_log** — 包含外键 CASCADE 约束及 3 个索引

### 旧表保护（DatabaseInitializer）

启动时先只读检查 `to_regclass('public.mistake')`。若旧关系名存在，则抛出初始化错误；旧表、关联数据、外键和迁移历史均不改动。不再自动重命名或删除旧表。必须备份并通过父项 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 的显式升级流程验收后才可恢复服务。查询错误也中止初始化。无旧表时，既有的列名修复、stamp 和 Common initializer 路径仍运行；它们不属于本保护修复的范围。

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

旧表中的 `date`、`image_paths`、`fingerprint` 等字段由本保护原样保留；不可把启动拒绝当成已完成的数据迁移。

## 20260612030015_AddStudentIdToProcessLog

**日期**: 2026-06-12
**类型**: 增量迁移

### 变更内容

1. **process_log** — 新增 `student_id` 列 (`VARCHAR(36)`, NULL) 及索引 `IX_process_log_student_id`
2. **mistake_practice** — `image_paths` 列类型从 `text` 改为 `jsonb`

对于尚未应用本迁移的正规 EF 历史链，类型变更显式使用 `USING image_paths::jsonb`；回退到 InitialCreate 使用 `USING image_paths::text`。仅承诺 JSON 数组的路径字符串语义保留，不承诺 JSON 文本空白字节一致。已经真实应用本迁移的数据库不会重执行。非法 JSON 会使本迁移在 EF 事务中失败并回滚新增列、索引和历史记录；保留原始文本，先备份并修正非法数据，再运行 `MigrateAsync` 重试。不要使用 `EnsureCreated`、手工添加 `student_id` 或手工 stamp 绕过失败。本修复不处理历史库错误 stamp，也不代表启动初始化器的旧 schema 升级已安全。

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
