# mistake_practice — 错题练习记录表

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PK, NOT NULL | — | 主键 |
| `mistake_item_id` | `UUID` | NOT NULL, FK, INDEX | — | 关联错题ID |
| `student_id` | `VARCHAR(36)` | NOT NULL, INDEX | — | 学生ID |
| `practice_date` | `TIMESTAMPTZ` | NOT NULL, INDEX | — | 练习时间 |
| `image_paths` | `TEXT` | NOT NULL | `'[]'` | 练习图片路径数组，JSON 格式 |
| `student_answer` | `TEXT` | NULL | — | 学生答案 |
| `is_correct` | `BOOLEAN` | NOT NULL | — | 是否正确 |
| `comments` | `TEXT` | NULL | — | 备注 |

## 索引

| 索引名 | 字段 | 类型 |
|--------|------|------|
| `PK_mistake_practice` | `id` | 主键 |
| `IX_mistake_practice_mistake_item_id` | `mistake_item_id` | 普通 |
| `IX_mistake_practice_student_id` | `student_id` | 普通 |
| `IX_mistake_practice_practice_date` | `practice_date` | 普通 |

## 外键关系

| 外键名 | 字段 | 引用表 | 删除行为 |
|--------|------|--------|----------|
| `FK_mistake_practice_mistake_item_mistake_item_id` | `mistake_item_id` | `mistake_item(id)` | CASCADE |

## 特殊说明

- `image_paths` 存储为 JSON 字符串数组（无命名策略），类型标记为 `TEXT` 但在内存中为 `List<string>`
- 代码位于：[MistakePracticeEntity.cs](file://src/Database/Entities/MistakePracticeEntity.cs)