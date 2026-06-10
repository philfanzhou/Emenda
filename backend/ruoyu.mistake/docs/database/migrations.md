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

## 相关文档

- [表结构文档](./tables/)
- [实体关系](./relations.md)
- [总览](./README.md)
