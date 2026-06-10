# process_log — 处理日志表

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PK, NOT NULL | — | 主键 |
| `mistake_item_id` | `UUID` | NOT NULL, FK, INDEX | — | 关联错题ID |
| `action` | `INT` | NOT NULL | — | 操作类型（枚举 ProcessAction） |
| `details` | `TEXT` | NULL | — | 操作详情 |
| `created_at` | `TIMESTAMPTZ` | NOT NULL, INDEX | — | 创建时间 |

## 索引

| 索引名 | 字段 | 类型 |
|--------|------|------|
| `PK_process_log` | `id` | 主键 |
| `IX_process_log_mistake_item_id_created_at` | `mistake_item_id, created_at` | 复合 |
| `IX_process_log_created_at` | `created_at` | 普通 |

## 外键关系

| 外键名 | 字段 | 引用表 | 删除行为 |
|--------|------|--------|----------|
| `FK_process_log_mistake_item_mistake_item_id` | `mistake_item_id` | `mistake_item(id)` | CASCADE |

## ProcessAction 枚举值

| 值 | 名称 | 说明 |
|----|------|------|
| 0 | Created | 创建错题 |
| 1 | ReviewConfirmed | 审核确认 |
| 2 | ReviewRejected | 审核驳回 |
| 3 | Practiced | 练习 |
| 4 | Analyzed | 分析 |

## 特殊说明

- 代码位于：[ProcessLogEntity.cs](file://src/Database/Entities/ProcessLogEntity.cs)