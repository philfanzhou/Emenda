# 数据库文档

> **本目录是 ruoyu.mistake 数据库结构的唯一事实源。**

## 表清单

| 表名 | 说明 | 详细文档 |
|------|------|----------|
| `mistake_item` | 错题条目，核心业务表 | [tables/mistake_item.md](./tables/mistake_item.md) |
| `mistake_practice` | 错题练习记录 | [tables/mistake_practice.md](./tables/mistake_practice.md) |
| `process_log` | 处理日志，记录错题生命周期 | [tables/process_log.md](./tables/process_log.md) |
| `reanalyze_job` | 错题再分析任务 | [tables/reanalyze_job.md](./tables/reanalyze_job.md) |
| `source_handoff` | 完整来源交接固定意图与原回执 | [tables/source_handoff.md](./tables/source_handoff.md) |

## 实体关系

```
mistake_item (1) ────< (N) mistake_practice  [CASCADE]
mistake_item (1) ────< (N) process_log       [CASCADE]
```

详见 [relations.md](./relations.md)

## 枚举值

### ReviewStatus（mistake_item.review_status）

| 值 | 名称 | 说明 |
|----|------|------|
| 0 | UNSPECIFIED | 未指定 |
| 1 | PendingReview | 待审核 |
| 2 | Confirmed | 已确认 |
| 3 | Rejected | 已退回 |

### ProcessAction（process_log.action）

| 值 | 名称 | 说明 |
|----|------|------|
| 0 | UNSPECIFIED | 未指定 |
| 1 | Created | 创建错题 |
| 2 | Analyzed | 分析 |
| 3 | ReviewConfirmed | 审核确认 |
| 4 | ReviewRejected | 审核退回 |
| 5 | Practiced | 练习 |
| 6 | Mastered | 已掌握 |

## 迁移历史

- `20260514051544_InitialCreate`：初始业务表与索引
- `20260612030015_AddStudentIdToProcessLog`：日志 student_id 与练习路径 jsonb
- `20260626000000_AddReanalyzeJob`：再分析任务
- `20260929000001_RenameReviewCommentToRootCause`：版本化根因列重命名
- `20260930000001_LeaseReanalyzeJobs`：全局领取租约与终态栅栏

普通启动只读核验结构和历史；全新空库须显式执行 `--upgrade-empty-db`。精确匹配 EF 历史链的带数据旧库使用 `--upgrade-known-db`，详见[迁移历史](./migrations.md)和[部署说明](../development/Deployment.md)。

## 数据库类型

PostgreSQL（Npgsql 提供程序）。

- [来源入库数据](./tables/source_intake.md)
