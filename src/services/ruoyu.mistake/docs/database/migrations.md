# 迁移历史

## 20260514051544_InitialCreate

**日期**: 2026-05-14
**类型**: 初始创建

### 创建的表

1. **mistake_item** — 包含所有字段及 6 个索引
2. **mistake_practice** — 包含外键 CASCADE 约束及 4 个索引
3. **process_log** — 包含外键 CASCADE 约束及 3 个索引

### 启动与显式升级

普通 Host 启动只在只读事务中读取 PostgreSQL catalog 和迁移历史，核对关系、列、类型、空值性、默认值、索引及约束。空库、旧 `public.mistake`、不完整或未知结构及不一致历史都拒绝启动，数据库保持原状。只接受两种形态：前三条 EF 迁移历史加上历史运行时已重命名的 `root_cause`，或下述四条 EF 迁移历史的目标结构。

创建全新空库后须单独执行 `--upgrade-empty-db`。该命令以 advisory transaction lock 串行化，仅在 public schema 为空时于单个事务中应用迁移并核验目标；目标已存在时可重复执行。其余形态不修改。运行前停止应用写入并备份；旧表或混合结构的带数据升级仍须按父项 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 另行设计和验收，不能用此空库命令处理。

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

### 历史兼容边界

旧版本可能在启动时直接添加 `student_id` 并错误地只记录 InitialCreate。此类结构与迁移历史不一致，新的只读启动会拒绝，且不会自动 stamp。先备份并核验实际列、`image_paths` 类型和历史，再按 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 处理；不要手工补写历史记录。

### 新增列

| 列名 | 类型 | 所在表 |
|------|------|--------|
| `student_id` | `VARCHAR(36)` NULL | process_log |

### 类型变更

| 列名 | 旧类型 | 新类型 | 所在表 |
|------|--------|--------|--------|
| `image_paths` | `text` | `jsonb` | mistake_practice |

## 20260929000001_RenameReviewCommentToRootCause

**类型**：版本化列重命名。新建空库按迁移链将 `mistake_item.review_comment` 重命名为 `root_cause`，并写入第四条 EF 历史记录。此前运行时已重命名且只有前三条历史记录的已知结构允许只读启动；不会在启动时补写第四条记录，也不对带数据旧库执行此迁移。目标字段为纯文本，保存错题根因。

## 相关文档

- [表结构文档](./tables/)
- [实体关系](./relations.md)
- [总览](./README.md)
