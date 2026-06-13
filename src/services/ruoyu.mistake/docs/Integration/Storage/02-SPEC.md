# 接口规格

## 配置参数

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|------|------|------|--------|------|
| `ConnectionStrings:Default` | string | PostgreSQL 必填 | `Data Source=data/sqlite/ruoyu_study_mistake.db` | 数据库连接字符串。含 `Host=` 或 `Server=` 时使用 PostgreSQL，否则使用 SQLite |

## 表结构定义

### mistake_item

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `student_id` | varchar(36) | NOT NULL | 学生 ID |
| `subject` | integer | NOT NULL | 科目 |
| `grade` | integer | NOT NULL | 年级 |
| `source_upload_id` | varchar(64) | NOT NULL, DEFAULT '' | 来源上传 ID |
| `source_regions` | JSONB | NOT NULL, DEFAULT '[]' | 来源区域信息 |
| `review_status` | integer | NOT NULL | 审核状态 |
| `reviewer_id` | varchar(36) | NULL | 审核人 ID |
| `reviewed_at` | timestamptz | NULL | 审核时间 |
| `review_comment` | text | NULL | 审核评论 |
| `type` | integer | NOT NULL, DEFAULT 0 | 错题类型 |
| `question_id` | varchar(36) | NULL | 关联题目 ID |
| `created_at` | timestamptz | NOT NULL | 创建时间 |
| `updated_at` | timestamptz | NULL, ConcurrencyCheck | 更新时间（乐观并发） |

### mistake_practice

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `mistake_item_id` | UUID | NOT NULL, FK → mistake_item(id) ON DELETE CASCADE | 关联错题 ID |
| `student_id` | varchar(36) | NOT NULL | 学生 ID |
| `practice_date` | timestamptz | NOT NULL | 练习日期 |
| `image_paths` | JSONB | NOT NULL, DEFAULT '[]' | 练习图片路径列表 |
| `student_answer` | text | NULL | 学生答案 |
| `is_correct` | boolean | NOT NULL | 是否正确 |
| `comments` | text | NULL | 评论 |

### process_log

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `mistake_item_id` | UUID | NOT NULL, FK → mistake_item(id) ON DELETE CASCADE | 关联错题 ID |
| `action` | integer | NOT NULL | 操作类型 |
| `details` | text | NULL | 操作详情 |
| `created_at` | timestamptz | NOT NULL | 创建时间 |

## 索引定义

### mistake_item

| 索引名 | 列 | 说明 |
|--------|----|------|
| `IX_mistake_item_student_id` | `student_id` | 按学生查询 |
| `IX_mistake_item_student_id_subject_grade_review_status` | `student_id, subject, grade, review_status` | 复合条件查询 |
| `IX_mistake_item_source_upload_id` | `source_upload_id` | 按上传来源查询 |
| `IX_mistake_item_review_status` | `review_status` | 按审核状态查询 |
| `IX_mistake_item_created_at` | `created_at` | 按时间排序 |

### mistake_practice

| 索引名 | 列 | 说明 |
|--------|----|------|
| `IX_mistake_practice_mistake_item_id` | `mistake_item_id` | 关联查询 |
| `IX_mistake_practice_student_id` | `student_id` | 按学生查询 |
| `IX_mistake_practice_practice_date` | `practice_date` | 按练习日期查询 |

### process_log

| 索引名 | 列 | 说明 |
|--------|----|------|
| `IX_process_log_mistake_item_id_created_at` | `mistake_item_id, created_at` | 按错题查日志并排序 |
| `IX_process_log_created_at` | `created_at` | 按时间排序 |

## JSONB 列格式

### source_regions

```json
[
  {
    "sourceImagePath": "uploads/2026/01/image001.jpg",
    "boundingBox": {
      "x1": 100,
      "y1": 200,
      "x2": 300,
      "y2": 400
    }
  }
]
```

- 字段采用 camelCase 命名（由 `JsonNamingPolicy.CamelCase` 控制）
- `boundingBox` 可为 null
- 顶层为数组，默认空数组 `[]`

### image_paths

```json
[
  "uploads/2026/01/practice001.jpg",
  "uploads/2026/01/practice002.jpg"
]
```

- 顶层为字符串数组，默认空数组 `[]`
- 序列化时不使用命名策略（`JsonSerializerOptions = null`）
