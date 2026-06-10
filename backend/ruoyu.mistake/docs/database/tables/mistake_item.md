# mistake_item — 错题条目表

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PK, NOT NULL | — | 主键 |
| `student_id` | `VARCHAR(36)` | NOT NULL, INDEX | — | 学生ID |
| `subject` | `INT` | NOT NULL | — | 学科（1-9） |
| `grade` | `INT` | NOT NULL | — | 年级（1-12） |
| `source_upload_id` | `VARCHAR(64)` | NOT NULL, INDEX | — | 上传记录ID，关联 uploads 桶 |
| `source_regions` | `JSONB` | NOT NULL | `'[]'` | 图片区域数组，JSON 格式：`[{"SourceImagePath":"...","BoundingBox":{...}}]` |
| `review_status` | `INT` | NOT NULL, INDEX | — | 审核状态（0=PendingReview, 1=Confirmed, 2=Rejected） |
| `reviewer_id` | `VARCHAR(36)` | NULL | — | 审核人ID |
| `reviewed_at` | `TIMESTAMPTZ` | NULL | — | 审核时间 |
| `review_comment` | `TEXT` | NULL | — | 审核意见 |
| `type` | `INT` | NOT NULL | `0` | 错题类型（预留，当前未使用） |
| `question_id` | `VARCHAR(36)` | NULL | — | 关联的题目ID（预留） |
| `created_at` | `TIMESTAMPTZ` | NOT NULL, INDEX | — | 创建时间 |
| `updated_at` | `TIMESTAMPTZ` | NULL | — | 最后更新时间 |

## 索引

| 索引名 | 字段 | 类型 |
|--------|------|------|
| `PK_mistake_item` | `id` | 主键 |
| `IX_mistake_item_student_id` | `student_id` | 普通 |
| `IX_mistake_item_student_id_subject_grade_review_status` | `student_id, subject, grade, review_status` | 复合 |
| `IX_mistake_item_source_upload_id` | `source_upload_id` | 普通 |
| `IX_mistake_item_review_status` | `review_status` | 普通 |
| `IX_mistake_item_created_at` | `created_at` | 普通 |

## 外键关系

- 无外键（被 `mistake_practice` 和 `process_log` 引用）

## 特殊说明

- `source_regions` 使用 JSONB 存储，序列化为骆驼命名（camelCase）的 JSON 数组
- `updated_at` 字段使用 `[ConcurrencyCheck]` 乐观并发控制
- 代码位于：[MistakeItemEntity.cs](file://src/Database/Entities/MistakeItemEntity.cs)