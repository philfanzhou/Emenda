# 实体关系图

```
┌─────────────────────────────────┐
│         mistake_item            │
│─────────────────────────────────│
│ id (PK)                         │
│ student_id                      │
│ subject                         │
│ grade                           │
│ source_upload_id                │
│ source_regions (JSONB)          │
│ review_status                   │
│ reviewer_id                     │
│ reviewed_at                     │
│ review_comment                  │
│ type                            │
│ question_id                     │
│ created_at                      │
│ updated_at                      │
└───────────┬─────────────────────┘
            │
            │ 1:N (CASCADE)
            │
     ┌──────┴──────────┐
     │                 │
     ▼                 ▼
┌──────────────────┐  ┌──────────────────┐
│ mistake_practice │  │   process_log    │
│──────────────────│  │──────────────────│
│ id (PK)          │  │ id (PK)          │
│ mistake_item_id  │  │ mistake_item_id  │
│   (FK → item.id) │  │   (FK → item.id) │
│ student_id       │  │ action           │
│ practice_date    │  │ details          │
│ image_paths (JSON)│ │ created_at       │
│ student_answer   │  └──────────────────┘
│ is_correct       │
│ comments         │
└──────────────────┘
```

## 关系说明

| 关系 | 类型 | 删除行为 | 说明 |
|------|------|----------|------|
| mistake_item → mistake_practice | 1:N | CASCADE | 删除错题时自动删除所有练习记录 |
| mistake_item → process_log | 1:N | CASCADE | 删除错题时自动删除所有处理日志 |

## JSONB 字段结构

### mistake_item.source_regions

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
  }
]
```

### mistake_practice.image_paths

```json
["practice/student123/answer1.jpg", "practice/student123/answer2.jpg"]
```