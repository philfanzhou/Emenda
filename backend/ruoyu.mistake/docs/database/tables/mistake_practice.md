# mistake_practice — 错题练习记录表

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PK, NOT NULL | — | 主键，客户端生成（ValueGeneratedNever） |
| `mistake_item_id` | `UUID` | NOT NULL, FK | — | 关联错题ID，见外键关系 |
| `student_id` | `VARCHAR(36)` | NOT NULL | — | 学生ID |
| `practice_date` | `TIMESTAMPTZ` | NOT NULL | `now()` | 练习时间 |
| `image_paths` | `JSONB` | NOT NULL | — | 练习图片路径数组，见下方 JSONB 格式示例 |
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

| 外键名 | 字段 | 引用表.字段 | 删除行为 |
|--------|------|-------------|----------|
| `FK_mistake_practice_mistake_item_mistake_item_id` | `mistake_item_id` | `mistake_item(id)` | CASCADE |

## JSONB 字段格式

### image_paths

使用默认序列化策略（PascalCase）：

```json
["practice/student123/answer1.jpg", "practice/student123/answer2.jpg"]
```

> **注意**: 与 [mistake_item.source_regions](./mistake_item.md) 的 camelCase 序列化不同，`image_paths` 使用默认的 PascalCase 序列化。由于数组元素为简单字符串，大小写差异不影响实际存储。

## 导航属性

| 属性名 | 类型 | 说明 |
|--------|------|------|
| `MistakeItem` | `MistakeItemEntity` | 关联的错题条目 |

## 特殊说明

- `id` 由客户端生成，EF Core 配置为 `ValueGeneratedNever`
- 删除关联的 [mistake_item](./mistake_item.md) 记录时，本表对应记录自动级联删除
- 实体关系：[relations.md](../relations.md)
