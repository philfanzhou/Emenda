# 旧错题退回日志

`legacy_return`、`legacy_return_object`、`legacy_return_attempt` 保存未来旧条目退回的完整恢复事实，不回填已经丢失关联的历史退回。仍有旧来源 ID 但没有实际 `source_intake_group` 的条目也属于此范围；受管条目继续使用原 `source_return` 及其真实来源外键。

## 数据结构

| 表 | 主键与内容 |
|---|---|
| `legacy_return` | `item_id` UUID 主键，RESTRICT 关联真实 `mistake_item`。唯一 `operation_id=mistake-return:{itemId:D}`；固定 student/actor/canonical、完整原条目 JSON、reason/rootCause/有效 grade/subject、UTC created_at；owner_id/fence/lease_until；phase、固定 payload/digest、error_kind、confirmed_record_id/receipt_created_at/completed_at/final_log_id。最后日志的 RESTRICT 外键延期至提交时检查。 |
| `legacy_return_object` | `(item_id,position)` 主键，RESTRICT 关联操作；按原 SourceRegions 顺序首次精确去重的 source_path；成对可空 expected_length/expected_sha256，捕获后不可替换；selected_attempt_id 只能绑定同图片的 Verified 候选一次。 |
| `legacy_return_attempt` | attempt_id UUID 主键，RESTRICT 关联图片；object_key 全局唯一且固定为 `uploads/mistake-returns/{itemId:D}/{attemptId:D}/original.jpg`；reservation 的 owner_id/fence、phase、copy_settled_at、verified_length/verified_sha256。 |

原快照包含全部真实条目元数据和原 source_upload_id（正常化后的空来源为空字符串），不创造来源 revision/snapshot。原图 JSON 按数据库原值保存，包括旧数据的额外成员和缺省字段；完成时只更新审核元数据，不重写原图 JSON。canonical 为 actor、reason（null归为空字符串）、rootCause（null与空字符串不同）、有效 grade/subject 的固定 JSON 数组；无效请求值采用原 grade/subject，包括原有的0（未指定），不接受其它任意无效新值。最终 rootCause 按请求写入，null 不保留旧值。

## 状态与保护

`Capturing` 先保存意图和全部原图，逐张一次绑定字节长度/SHA256；全部捕获才允许 `Prepared`。每次复制先创建 Reserved 候选。Unknown 永不选定；正常复制结束后先持久 CopySettled，再核验字节一致进入 Verified 并选定。全部选定后，保存有序 returned-payload-v1 载荷及摘要进入 `Sending`，后续不可替换。

固定 operation/student/digest/version、非零 recordId 和合法 UTC 历史回执由调用方验证后，在同一短事务提交 `Completed`、原条目 `Rejected` 和恰一条新最终日志。原日志、练习、原图和所有候选永久保留。确定冲突进入 `Conflict`，停止推进；未完成与成功严格分开。

数据库时钟领取60秒租约，独立调用方每20秒续期；接管递增 fence。`LegacyReturnJournal` 每次写入都先锁实际 item，再核对 owner/fence/未到期租约，不持外部 IO 事务。提交响应未知时调用方必须重新读取真实数据库，不根据本地对象宣称完成或发起外部写。

自意图存在，数据库拒绝旧 SQL/EF 对同 item 的确认、元数据/归属/来源/图片修改、删除/复活，以及练习、日志和重分析任务的增删改。受管来源组与旧退回互斥。只有完全匹配的首次完成允许变更审核元数据和追加最终日志；延期完整性检查拒绝半完成提交。相关表 TRUNCATE 也受保护，其它条目正常操作。没有连接级绕过开关。

## 升级、关闭与恢复

`20261010000001_AddLegacyReturns` 是第12条迁移。启动从空库、精确已知1–11版本及既有支持旧形态，在原 advisory lock 和整库事务内升级；未知/部分表、坏函数/trigger/FK/history 拒绝且无写，目标重复启动不写。新三表初始为空，不改原业务行、ID或归档事实。

审核、对象复制和 Student 原历史 receipt 确认已通过 [SourceReturns](../../modules/SourceReturns.md) 接入；默认关闭 LegacyReturns 新领取和 worker 推进。关闭时须保留日志、guards和完整图片引用，不回到先删除的旧路径。有任一新操作时 Down 拒绝；回退保留认识当前 schema 的 binary 并停止推进。恢复使用整个数据库 pg_dump/restore，包含三表、业务、旧归档、迁移 history、所有函数/触发器和永久 pins；不能只导出载荷或图片路径。

[StorageReferences v1](../../../../../../docs/integrations/StorageReferences.md) 的既有 return/return-object/return-attempt coverage 同时覆盖新操作原图、固定 payload、全部 object 和全部 attempt，包括 Unknown/未选/Completed。wire版本、固定coverage、摘要算法和 deletionAuthorized=false 不变；缺表、坏行或数据库故障不得发布部分快照。

验证用例为 `LegacyReturnJournalPostgresTests`，用任务自有 PostgreSQL 15、随机库与高位端口执行版本升级、状态约束、并发争抢、原子完成、完整引用和整库备份恢复；专项跳过不算通过。

```bash
# Dedicated local resources; never point these values to business databases.
# MISTAKE_LEGACY_RETURN_TEST_CONNECTION: owned PostgreSQL 15 admin connection
# MISTAKE_LEGACY_RETURN_TEST_CONTAINER: its task-owned container, used by pg_dump/pg_restore
# Tests create/drop random databases only.
dotnet test tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests/Ruoyu.Study.Mistake.Provider.Tests.csproj --configuration Release --filter FullyQualifiedName~LegacyReturnJournalPostgresTests
```
