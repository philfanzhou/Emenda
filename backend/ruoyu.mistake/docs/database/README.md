# 数据库文档

> **本目录是 ruoyu.mistake 数据库结构的唯一事实源。**

## 表清单

| 表名 | 说明 | 详细文档 |
|------|------|----------|
| `mistake_item` | 错题条目，核心业务表 | [tables/mistake_item.md](./tables/mistake_item.md) |
| `mistake_practice` | 错题练习记录 | [tables/mistake_practice.md](./tables/mistake_practice.md) |
| `process_log` | 处理日志，记录错题生命周期 | [tables/process_log.md](./tables/process_log.md) |

## 实体关系

```
mistake_item (1) ────< (N) mistake_practice  [CASCADE]
mistake_item (1) ────< (N) process_log       [CASCADE]
```

详见 [relations.md](./relations.md)

## 迁移历史

- **20260514051544_InitialCreate**: 初始创建三张表及所有索引

详见 [migrations.md](./migrations.md)

## 数据库类型

- 生产环境：PostgreSQL
- 开发环境：SQLite（自动识别，跳过迁移逻辑）

## 旧表迁移

旧表 `mistake` 已通过 `DatabaseInitializer.MigrateOldSchemaAsync` 迁移到 `mistake_item`，主要变更：
- 表名 `mistake` → `mistake_item`
- 列 `status` → `review_status`
- 新增 `source_upload_id`, `source_regions`, `reviewer_id`, `reviewed_at`, `review_comment`, `type`, `question_id`
- 移除 `date`, `image_paths`, `fingerprint`