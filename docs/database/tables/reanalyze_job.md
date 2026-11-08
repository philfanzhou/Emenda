# reanalyze_job

`id`（uuid）为稳定 JobId；`item_id` 是错题引用，不设外键，因此条目删除后仍可保留失败任务。`reviewer_id`、`student_id`、可选 `teacher_description` 与 `previous_root_cause` 在提交时保存。`previous_root_cause` 是提交时审计快照；执行输入使用领取时的 `execution_root_cause`。

| 字段 | 类型 | 含义 |
|---|---|---|
| `status` | integer | 1 Pending、2 Running、3 Completed、4 Failed |
| `owner_id` | uuid nullable | 每次领取生成的随机执行者标识；终态清空 |
| `lease_until` | timestamptz nullable | PostgreSQL 时钟决定的有效期；终态清空 |
| `fencing_version` | bigint not null，默认 0 | 每次领取递增，迟到执行者不能终态写入 |
| `attempt` | integer not null，默认 0 | 领取次数，上限 3 |
| `execution_root_cause` | text nullable | 真正执行时 Item 根因 |
| `execution_input_digest` | varchar(64) nullable | StudentId、SourceRegions、根因和审核状态的 SHA-256 摘要 |
| `error_message`、`new_root_cause` | text nullable | 终态原因码或成功结果 |
| `created_at`、`started_at`、`completed_at` | timestamptz | 提交、最近一次领取、终态时间 |
| `vl_duration_ms` | bigint nullable | 成功 VL 耗时 |

索引按 `status,created_at`、`reviewer_id,status`、`completed_at`。全局领取通过 PostgreSQL transaction advisory lock、Job/Item 行锁及条件更新实现；同题顺序由 `created_at,id` 确定。仅 Completed/Failed 且 `completed_at` 超过 30 天删除。执行模型见[重分析能力](../../modules/MistakeReview/ReanalyzeMistakeItem/README.md)。
