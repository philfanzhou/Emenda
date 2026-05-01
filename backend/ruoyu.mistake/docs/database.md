# 错题本服务数据库设计

## 单库设计 (ruoyu_study_mistake)

所有错题相关的数据存储在单一 PostgreSQL 数据库中。
此数据库已与 `ruoyu_study_questionbank` 实现物理分离。

```sql
-- mistake 错题表
mistake (
    id              UUID PRIMARY KEY,
    student_id      VARCHAR(36) NOT NULL,
    subject         INT NOT NULL,
    grade           INT NOT NULL,
    date            TIMESTAMP WITH TIME ZONE NOT NULL,
    type            INT NOT NULL,
    question_id     VARCHAR(36),      -- 关联的题库ID，逻辑关联无外键
    status          INT NOT NULL,     -- 状态：1=PENDING, 2=PROCESSED
    created_at      TIMESTAMP WITH TIME ZONE NOT NULL,
    updated_at      TIMESTAMP WITH TIME ZONE
)

-- mistake_detail 错题详情表
mistake_detail (
    id              UUID PRIMARY KEY,
    mistake_id      UUID NOT NULL REFERENCES mistake(id) ON DELETE CASCADE,
    original_images JSONB,             -- 原始上传图片路径，JSON数组
    comments        TEXT
)

-- mistake_practice 错题重答与举一反三表
mistake_practice (
    id              UUID PRIMARY KEY,
    mistake_id      UUID NOT NULL REFERENCES mistake(id) ON DELETE CASCADE,
    student_id      VARCHAR(36) NOT NULL,
    practice_date   TIMESTAMP WITH TIME ZONE NOT NULL,
    image_paths     JSONB,             -- 学生重答上传的图片，JSON数组
    student_answer  TEXT,
    is_correct      BOOLEAN NOT NULL,
    comments        TEXT
)

-- process_log 错题处理日志表
process_log (
    id              UUID PRIMARY KEY,
    mistake_id      UUID NOT NULL REFERENCES mistake(id) ON DELETE CASCADE,
    action          INT NOT NULL,
    details         TEXT,
    created_at      TIMESTAMP WITH TIME ZONE NOT NULL
)
```

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
