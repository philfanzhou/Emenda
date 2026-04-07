# 错题本服务数据库设计

## 单库设计 (ruoyu_study_mistake)

所有错题相关的数据存储在单一 PostgreSQL 数据库中。
此数据库已与 `ruoyu_study_questionbank` 实现物理分离。

```sql
-- mistake 错题表
mistake (
    id              VARCHAR(36) PRIMARY KEY,
    student_id      VARCHAR(36) NOT NULL,
    subject         INT NOT NULL,
    grade           INT NOT NULL,
    date            TIMESTAMP WITH TIME ZONE NOT NULL,
    image_paths     TEXT,             -- 错题原图路径，JSON数组
    content         TEXT,
    correct_answer  VARCHAR(500),
    student_answer  VARCHAR(500),
    type            INT NOT NULL,
    question_id     VARCHAR(36),      -- 关联的题库ID，逻辑关联无外键
    comments        TEXT,
    status          INT NOT NULL,     -- 状态：1=PENDING, 2=PROCESSED
    created_at      TIMESTAMP WITH TIME ZONE NOT NULL,
    updated_at      TIMESTAMP WITH TIME ZONE
)

-- mistake_practice 错题重答与举一反三表
mistake_practice (
    id              VARCHAR(36) PRIMARY KEY,
    mistake_id      VARCHAR(36) NOT NULL REFERENCES mistake(id) ON DELETE CASCADE,
    student_id      VARCHAR(36) NOT NULL,
    practice_date   TIMESTAMP WITH TIME ZONE NOT NULL,
    image_paths     TEXT,             -- 学生重答上传的图片，JSON数组
    student_answer  TEXT,
    is_correct      BOOLEAN NOT NULL,
    comments        TEXT
)

-- process_log 错题处理日志表
process_log (
    id              VARCHAR(36) PRIMARY KEY,
    mistake_id      VARCHAR(36) NOT NULL REFERENCES mistake(id) ON DELETE CASCADE,
    action          INT NOT NULL,
    details         TEXT,
    created_at      TIMESTAMP WITH TIME ZONE NOT NULL
)
```

## 对象存储 (MinIO)

错题图片、重答图片等存储在 MinIO 中。

### 存储路径格式

```
m_{guid}_{index}.jpg
```
