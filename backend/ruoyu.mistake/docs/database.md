# 错题本服务数据库设计

## 单库设计 (ruoyu_study_mistake)

所有错题相关的数据存储在单一 PostgreSQL 数据库中。
此数据库已与 `ruoyu_study_questionbank` 实现物理分离。

```sql
-- mistake_item 错题条目表
mistake_item (
    id                  UUID PRIMARY KEY,
    student_id          VARCHAR(36) NOT NULL,
    subject             INT NOT NULL,             -- 学科归属（错题条目级别）
    grade               INT NOT NULL,             -- 年级归属（错题条目级别）
    source_upload_id    VARCHAR(36),              -- 来源上传记录 ID
    source_regions      JSONB,                    -- 图片区域引用，JSON数组
    review_status       INT NOT NULL DEFAULT 1,   -- 审核状态：1=PendingReview, 2=Confirmed, 3=Rejected
    reviewer_id         VARCHAR(36),              -- 审核人 ID
    reviewed_at         TIMESTAMP WITH TIME ZONE, -- 审核时间
    review_comment      TEXT,                     -- 审核意见
    type                INT NOT NULL DEFAULT 0,   -- 错题类型
    question_id         VARCHAR(36),              -- 关联题库 ID
    created_at          TIMESTAMP WITH TIME ZONE NOT NULL,
    updated_at          TIMESTAMP WITH TIME ZONE
)

-- mistake_practice 错题练习记录表
mistake_practice (
    id              UUID PRIMARY KEY,
    mistake_id      UUID NOT NULL REFERENCES mistake_item(id) ON DELETE CASCADE,
    student_id      VARCHAR(36) NOT NULL,
    practice_date   TIMESTAMP WITH TIME ZONE NOT NULL,
    image_paths     JSONB,             -- 学生重答上传的图片，JSON数组
    student_answer  TEXT,
    is_correct      BOOLEAN NOT NULL,
    comments        TEXT,
    created_at      TIMESTAMP WITH TIME ZONE NOT NULL
)
```

**关键设计说明**：

`subject` 和 `grade` 字段位于 `mistake_item` 表（业务实体级别），而非 `image_upload_record` 表（图片容器级别）。这允许同一上传记录中的不同图片被分配到不同学科/年级的错题条目。详见 [database-spec.md](../../../docs/database-spec.md) 中的"数据模型变更"章节。

### 索引

```sql
-- mistake_item 索引
CREATE INDEX idx_mistake_item_student_id ON mistake_item(student_id);
CREATE INDEX idx_mistake_item_source_upload_id ON mistake_item(source_upload_id);
CREATE INDEX idx_mistake_item_review_status ON mistake_item(review_status);
CREATE INDEX idx_mistake_item_subject_grade ON mistake_item(subject, grade);

-- mistake_practice 索引
CREATE INDEX idx_mistake_practice_mistake_id ON mistake_practice(mistake_id);
CREATE INDEX idx_mistake_practice_student_id ON mistake_practice(student_id);
```

### 实体关系

- 一个学生可以有多条错题条目（一对多）
- 一条上传记录可以关联多条错题条目（通过 source_upload_id，一对多）
- 一条错题条目可以有多条练习记录（一对多，级联删除）

## 对象存储 (MinIO)

错题图片、重答图片等存储在 MinIO 中。

### 存储路径格式

```
mistakes/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/original_{index}.jpg
mistakes/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/practice_{practice_id}_{index}.jpg
```

### 路径设计说明

| 维度 | 说明 |
|------|------|
| `subject` | 科目维度，便于按科目查询和管理 |
| `student_id` | 学生维度，便于按学生查询和清理 |
| `year/month/day` | 时间维度，便于按时间归档和清理 |
| `mistake_id` | 错题维度，便于按错题查询和整题清理 |
| `original_` / `practice_` | 类型前缀，区分原图和练习图 |

### 示例路径

```
mistakes/1/student-abc/2026/05/01/3fa85f64-5717-4562-b3fc-2c963f66afa6/original_0.jpg
mistakes/1/student-abc/2026/05/01/3fa85f64-5717-4562-b3fc-2c963f66afa6/original_1.jpg
mistakes/1/student-abc/2026/05/01/3fa85f64-5717-4562-b3fc-2c963f66afa6/practice_7c9e6679-7425-40de-944b-e07fc1f90ae7_0.jpg
```

### 前缀查询

通过 S3 ListObjects 可快速定位：
- 按科目：`mistakes/{subject}/`
- 按学生：`mistakes/{subject}/{student_id}/`
- 按错题：`mistakes/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/`
- 仅原图：`mistakes/.../original_`
- 仅练习图：`mistakes/.../practice_`
