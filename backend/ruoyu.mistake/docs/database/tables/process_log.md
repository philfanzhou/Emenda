# process_log — 处理日志表

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PK, NOT NULL | — | 主键，客户端生成（ValueGeneratedNever） |
| `mistake_item_id` | `UUID` | NOT NULL, FK | — | 关联错题ID，见外键关系 |
| `action` | `INT` | NOT NULL | — | 操作类型，见 ProcessAction 枚举 |
| `details` | `TEXT` | NULL | — | 操作详情 |
| `created_at` | `TIMESTAMPTZ` | NOT NULL | `now()` | 创建时间 |

## 索引

| 索引名 | 字段 | 类型 |
|--------|------|------|
| `PK_process_log` | `id` | 主键 |
| `IX_process_log_mistake_item_id_created_at` | `mistake_item_id, created_at` | 复合 |
| `IX_process_log_created_at` | `created_at` | 普通 |

## 外键关系

| 外键名 | 字段 | 引用表.字段 | 删除行为 |
|--------|------|-------------|----------|
| `FK_process_log_mistake_item_mistake_item_id` | `mistake_item_id` | `mistake_item(id)` | CASCADE |

## ProcessAction 枚举

| 值 | 名称 | 说明 |
|----|------|------|
| 0 | UNSPECIFIED | 未指定 |
| 1 | Created | 创建错题 |
| 2 | Analyzed | 分析 |
| 3 | ReviewConfirmed | 审核确认 |
| 4 | ReviewRejected | 审核驳回 |
| 5 | Practiced | 练习 |
| 6 | Mastered | 已掌握 |

## 导航属性

| 属性名 | 类型 | 说明 |
|--------|------|------|
| `MistakeItem` | `MistakeItemEntity` | 关联的错题条目 |

## 特殊说明

- `id` 由客户端生成，EF Core 配置为 `ValueGeneratedNever`
- 删除关联的 [mistake_item](./mistake_item.md) 记录时，本表对应记录自动级联删除
- 复合索引 `IX_process_log_mistake_item_id_created_at` 支持按错题查询日志并按时间排序
- 实体关系：[relations.md](../relations.md)
