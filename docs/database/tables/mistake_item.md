# mistake_item — 错题条目表

## 字段清单

| 字段名 | 类型 | 约束 | 默认值 | 说明 |
|--------|------|------|--------|------|
| `id` | `UUID` | PK, NOT NULL | — | 主键，客户端生成（ValueGeneratedNever） |
| `student_id` | `VARCHAR(36)` | NOT NULL | — | 学生ID |
| `subject` | `INT` | NOT NULL | — | 学科 |
| `grade` | `INT` | NOT NULL | — | 年级 |
| `source_upload_id` | `VARCHAR(64)` | NOT NULL | — | 上传记录ID，关联 uploads 桶 |
| `source_regions` | `JSONB` | NOT NULL | — | 图片区域数组，见下方 JSONB 格式示例 |
| `review_status` | `INT` | NOT NULL | — | 审核状态，见 ReviewStatus 枚举 |
| `reviewer_id` | `VARCHAR(36)` | NULL | — | 审核人ID |
| `reviewed_at` | `TIMESTAMPTZ` | NULL | — | 审核时间 |
| `root_cause` | `TEXT` | NULL | — | 错误根因（AI 生成，老师审核时可修改） |
| `question_id` | `VARCHAR(36)` | NULL | — | 关联的题目ID |
| `created_at` | `TIMESTAMPTZ` | NOT NULL | `now()` | 创建时间 |
| `updated_at` | `TIMESTAMPTZ` | NULL, ConcurrencyCheck | — | 最后更新时间，乐观并发控制 |
| `conversation_active` | `BOOLEAN` | NOT NULL | `false` | 答疑自动采集的错题当前是否仍然有效 |
| `source_kind` | `TEXT` | NOT NULL, CHECK | — | 来源：`extraction` / `conversation` / `legacy` |
| `source_caller` | `TEXT` | NULL | — | 所属产品调用方；legacy 为 NULL |
| `content` | `JSONB` | NOT NULL | — | 统一题目内容（见下方说明） |
| `source_detail` | `JSONB` | NULL, CHECK | — | 来源明细；legacy 为 NULL，其余必有 |

v26（阶段 C）起这四列就是错题来源的唯一记录：旧列 `analysis_snapshot`、`conversation_source`、`conversation_caller`
和 v24 的推导触发器已删除。管道通过 `MistakeSource` 写入各自独有的部分（答疑的调用方和证据快照、提取的分析文字和保留文件），
`MistakeMaterializer` 在每次 EF 保存时补全其余部分：非答疑行的 `extraction`/`legacy`、提取任务及其调用方、
`source_detail` 的任务字段、答疑的题目文字，以及与 `source_regions` 一一对应的 `content.images`。
任务已删除的提取错题，`source_caller` 由 v25 迁移从删除回执的 `owner_hash` 反查；反查不到时为 NULL。
`source_detail`：提取为 `{ jobId, questionId, sourceVersion, retainedFiles }`（有分析时才有 `sourceVersion`，
任务删除后才有 `retainedFiles`）；答疑为完整证据快照，与原 `conversation_source` 逐字节相同。
`content` 的结构：

```json
{
  "version": 1,
  "statement": "题干，未知为 null",
  "studentAnswer": "学生作答，未知为 null",
  "referenceAnswer": "参考答案，未知为 null",
  "explanation": "解析，答疑来源为 null",
  "evidence": "判错依据，未知为 null",
  "knowledgePoints": [],
  "images": [{ "path": "mistakes/...", "role": "question 或 source", "box": [x1, y1, x2, y2] }]
}
```

`content.images[i].path` 始终等于 `source_regions[i].sourceImagePath`。`role=question` 是题目图，
`role=source` 是答疑中的原图（仅作证据）。设计见 [UnifiedMistakeIntake](../../design/UnifiedMistakeIntake.md)。

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

- 无外键（被 [process_log](./process_log.md) 引用）

## ReviewStatus 枚举

| 值 | 名称 | 说明 |
|----|------|------|
| 0 | UNSPECIFIED | 未指定 |
| 1 | PendingReview | 待审核 |
| 2 | Confirmed | 已确认 |
| 3 | Rejected | 已退回 |

## JSONB 字段格式

### source_regions

使用 camelCase 序列化策略：

```json
[
  {
    "sourceImagePath": "uploads/student123/region1.jpg",
    "boundingBox": {
      "x1": 100,
      "y1": 200,
      "x2": 300,
      "y2": 400
    }
  },
  {
    "sourceImagePath": "uploads/student123/region2.jpg",
    "boundingBox": null
  }
]
```

子结构说明：

- **SourceRegionEntry**: `sourceImagePath` (string), `boundingBox` (BoundingBoxEntry?)
- **BoundingBoxEntry**: `x1` (int), `y1` (int), `x2` (int), `y2` (int)

## 特殊说明

- `id` 由客户端生成，EF Core 配置为 `ValueGeneratedNever`
- `updated_at` 字段使用 `[ConcurrencyCheck]` 实现乐观并发控制
- `source_regions` 使用 JSONB 存储，序列化为 camelCase 命名的 JSON 数组
- 实体关系：[relations.md](../relations.md)
