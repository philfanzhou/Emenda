# 来源入库数据

来源语义的唯一权威是[SourceIntake](../../modules/SourceIntake.md)。三张本域表共同维护一个来源到实际业务结果的不变量，不引用 Student 数据库。

| 表 | 键与内容 |
|---|---|
| source_intake | PK (source_upload_id,content_revision)，完整 immutable manifest/snapshot/student/mode；owner UUID、递增 fence、DB lease_until、materialized |
| source_intake_group | PK item_id，来源 RESTRICT FK、actual item 与 Created log 的 deferred RESTRICT FK；group_hash 索引、完整 canonical、atoms、原显示 regions、创建 payload 与永久原结果 |
| source_intake_request | PK (可信actor,create action,request UUID)，来源 RESTRICT FK、完整 canonical 请求、已物化 item_ids；未完成请求保留绑定 |

原有 item/practice/log/job 数据不回填。不以 digest 代替完整比较；group/source/result 一旦物化不能改身份、DELETE 或 TRUNCATE。旧 item INSERT 与 source 登记使用同 canonical UUID advisory lock，旧写先赢明确 legacy_source_conflict；登记先赢只接受精确实际 result 映射的 item。延期完整性 guard 保证每个结果已有真实 item、恰一条 Created 日志及对应请求结果。

owner/source/regions 修改、受管 Reject/Delete、来源/结果/请求去关联均由 DB 拒绝；subject/grade/rootCause 与合法 Confirm、练习不冻结。租约依据数据库时钟，不代表 source pin TTL。启动时核对全部新增对象、函数体、触发器条件及 restrictive/deferred references，未知 catalog 原状拒绝。

## 选定图片目标与候选

[SourceTargets](../../modules/SourceTargets.md) 新增 `source_target_object`（PK source_upload_id/content_revision/source_path，snapshot/student/expected_length/SHA256、nullable selected_attempt_id）与 `source_target_attempt`（PK attempt_id、同 object 复合 FK、唯一 target_key、owner/fence、phase、verified_length/SHA256）。object→source、attempt→object、selected→attempt 全为 RESTRICT；先 object(null selected)、再 attempt、后 verified 与选定的写顺序避免循环引用。

object 插入必须绑定不可变 manifest 的实际图/hash/长度，并有已提交 group→真实 item 的精确 regions/student/source 关系。选定仅来自当前 source lease/fence 合法且内容校验字段完整匹配的 Verified attempt，一次后不可更新；attempt 键不可变，已选候选不可更新。DELETE/TRUNCATE与关键更新拒绝，所有未选候选也作为持久 pin 保留。

`guard_reserved_target_input` 对 item、practice、source 的 INSERT/UPDATE 拒绝 reserved 精确 namespace 及 legacy 换前缀输入；既有 source/group/request/item/log guards 保持。catalog 核对新增 tables/columns/indexes/RESTRICT FKs、完整函数体/触发器与历史。非空目标两表的 Down 拒绝，备份/restore必须包含两表及其 guards，不能只恢复 item 行。

## 持久退回

[SourceReturns](../../modules/SourceReturns.md) 增加 source_return（PK item_id/唯一 operation_id、实际 group/source RESTRICT 关系、immutable original/canonical、phase/固定 payload/digest、source owner/fence、历史 receipt 与 final tuple）、source_return_object（item/有序 position PK、实际 selected target/expected bytes、一次 selected attempt）、source_return_attempt（唯一 attempt/key、item/position RESTRICT、reservation owner/fence、CopySettled/verified 字节证据）。selected→attempt 和 final→log 为 RESTRICT，final log FK 延期检查。

数据库核验 actual group→source→snapshot/student、ordered first-path full 去重、原元数据、target选定身份、lease/fence、settled-before-verify-before-select、payload键/顺序/类型与 returned-payload-v1；全部 final tuple/item/log 在提交时延期检查。retained history 与新意图共同阻止所有 item/practice/process-log/reanalysis 写方绕过或复活。非空 return/对象/候选不允许 DELETE/TRUNCATE/Down，候选不设置 TTL。

当前精确历史为九条迁移；正常启动核验新增完整 catalog/函数体/触发器。known1–8 升级保留原行/字节/状态；未知 public.mistake 仍原状拒绝。包含全部 operation/object/attempt 和 guards 的备份恢复流程见[部署](../../development/Deployment.md#受管持久退回启用与恢复)。

## 完整交接封口

[source_handoff](./source_handoff.md)以现source lease/fence固定完整group集合、targets/canonical和原receipt。封口后的group/request新物化/terminal回退由最终guards拒绝；原完整创建结果可重放。source的新revision仅在所有旧managed handoff.Completed且历史student相同时接受，unmanaged旧item仍冲突。原source/target/return各保护继续成立。
