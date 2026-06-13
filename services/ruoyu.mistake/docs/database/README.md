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

## 枚举值

### ReviewStatus（mistake_item.review_status）

| 值 | 名称 | 说明 |
|----|------|------|
| 0 | UNSPECIFIED | 未指定 |
| 1 | PendingReview | 待审核 |
| 2 | Confirmed | 已确认 |
| 3 | Rejected | 已驳回 |

### ProcessAction（process_log.action）

| 值 | 名称 | 说明 |
|----|------|------|
| 0 | UNSPECIFIED | 未指定 |
| 1 | Created | 创建错题 |
| 2 | Analyzed | 分析 |
| 3 | ReviewConfirmed | 审核确认 |
| 4 | ReviewRejected | 审核驳回 |
| 5 | Practiced | 练习 |
| 6 | Mastered | 已掌握 |

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
- 列 `mistake_id` → `mistake_item_id`（process_log / mistake_practice）
- 新增 `source_upload_id`, `source_regions`, `reviewer_id`, `reviewed_at`, `review_comment`, `type`, `question_id`
- 移除 `date`, `image_paths`, `fingerprint`
