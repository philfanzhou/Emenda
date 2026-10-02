# 来源入库数据

来源语义的唯一权威是[SourceIntake](../../modules/SourceIntake.md)。三张本域表共同维护一个来源到实际业务结果的不变量，不引用 Student 数据库。

| 表 | 键与内容 |
|---|---|
| source_intake | PK (source_upload_id,content_revision)，完整 immutable manifest/snapshot/student/mode；owner UUID、递增 fence、DB lease_until、materialized |
| source_intake_group | PK item_id，来源 RESTRICT FK、actual item 与 Created log 的 deferred RESTRICT FK；group_hash 索引、完整 canonical、atoms、原显示 regions、创建 payload 与永久原结果 |
| source_intake_request | PK (可信actor,create action,request UUID)，来源 RESTRICT FK、完整 canonical 请求、已物化 item_ids；未完成请求保留绑定 |

原有 item/practice/log/job 数据不回填。不以 digest 代替完整比较；group/source/result 一旦物化不能改身份、DELETE 或 TRUNCATE。旧 item INSERT 与 source 登记使用同 canonical UUID advisory lock，旧写先赢明确 legacy_source_conflict；登记先赢只接受精确实际 result 映射的 item。延期完整性 guard 保证每个结果已有真实 item、恰一条 Created 日志及对应请求结果。

owner/source/regions 修改、受管 Reject/Delete、来源/结果/请求去关联均由 DB 拒绝；subject/grade/rootCause 与合法 Confirm、练习不冻结。租约依据数据库时钟，不代表 source pin TTL。普通启动核对全部新增对象、函数体、触发器条件及 restrictive/deferred references，未知 catalog 原状拒绝。

