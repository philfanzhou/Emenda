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
| `legacy_return` / `legacy_return_object` / `legacy_return_attempt` | 旧条目未来退回的持久日志与全部图片保护 | [tables/legacy_return.md](./tables/legacy_return.md) |
| `legacy_archive` | 旧结构收敛时被移除或改写数据的只追加归档 | [tables/legacy_archive.md](./tables/legacy_archive.md) |
| `layout_job` / `layout_job_image` / `layout_result` / `layout_result_region` / `layout_request` | 版面检测作业、每图进度、接受结果与请求键重放 | [tables/layout_job.md](./tables/layout_job.md) |

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
- `20261008000001_AddLegacyArchive`：旧结构收敛归档表（其余迁移见[迁移历史](./migrations.md)）

服务启动时自动把空库、已知 EF 历史的旧库和旧版自动维护留下的已知结构升级到当前结构，已是目标时不写入；不支持的结构拒绝启动且不修改。详见[迁移历史](./migrations.md)和[部署说明](../development/Deployment.md)。

## 数据库类型

PostgreSQL（Npgsql 提供程序）。

- [来源入库数据](./tables/source_intake.md)

[StorageReferences v1](../../../../../docs/integrations/StorageReferences.md)包含本域引用全集、shared snapshot schema/迁移、配置与验证门禁。
