# layout_job / layout_job_image / layout_result / layout_result_region / layout_request — 版面检测作业与接受结果

第十三条迁移（IKQA2O）新增的五张表：对既有受管来源版本的版面检测作业、每图工作项、接受的每图结果与区域、请求键重放记录。作业消费 [StructaDoc](../../Integration/StructaDoc.md) 版面检测客户端（ADR 见平台 `docs/adr/0023`）。

| 表 | 键与内容 |
|---|---|
| layout_job | PK job_id；(source_upload_id,content_revision) RESTRICT FK source_intake、(source,revision,result_revision) 唯一；完整 retained manifest、捕获的非秘密处理配置（credential_ref 只存配置键名 `Mistake:Layout:ApiKey`，永不存 Key）、PreserveSnapshotRotations 后的 image_rotations；owner UUID、递增 fence、DB lease_until、attempt、status（Queued/Running/Succeeded/Failed）、error_kind、created/completed_at |
| layout_job_image | PK (job_id,image_path)、position 顺序；expected_length/expected_sha256 为保留 manifest 字节身份（每次传输前重验）；status（Pending/Succeeded/Failed）、attempts（每图有界，上限 5）、last_error、retry_not_before、updated_at |
| layout_result | PK result_id；(job_id,image_path) 与 (source,revision,result_revision,image_path) 唯一——每代每图只有一个接受结果；自包含绑定 source/revision/snapshot、字节身份、相关 source rotation、产出 attempt、upstream_request_id、consumer 合同标识 `mistake-layout-v1`、coordinate_frame、width/height/orientation、engine/model/version 观察、有界 diagnostics（8 条 × 200 字符）与捕获配置副本 |
| layout_result_region | PK (result_id,region_id)；label 与 normalized box（top-left-origin-normalized 存储帧），region_id 即上游检测顺序 |
| layout_request | PK (actor,action,request_key)；canonical 为 mistake-layout-v1(source,revision)，job RESTRICT FK。同键同输入重放同一作业，不同输入 `request_payload_conflict`；换新键为独立 result revision |

## 执行与保护语义

- 领取/心跳/栅栏沿用 reanalyze_job 与 source_intake 模式：DB `clock_timestamp()` 租约（90 秒）、20 秒心跳 `RenewLease`（owner+fence+未过期三重守卫）、fenced 写在事务内 FOR UPDATE 重验 owner/fence/lease——失租的迟到尝试不能发布、失败记录、完成或取消更新一代。
- 推理永远在数据库事务外：字节按保留 length/SHA-256 重验后才传输，缺失或不匹配只失败该图且上游调用为零；不支持的格式（非 JPEG/PNG）本地显式失败，零请求。每图独立推进：已完成的图保持可见不再推理，仅重试 eligible 的失败/待处理图（有界退避，尊重上游 Retry-After）。
- 结果、区域与请求重放记录是只插入事实：guard_layout_immutable 拒绝 UPDATE/DELETE，guard_layout_truncate 拒绝 TRUNCATE。同键换新键产生独立 result revision，先前成功结果永久保留；旧 revision 结果只留在自己的 (source,revision,result_revision) 行，永不替换新结果。
- 不承诺上游 exactly-once：响应丢失可导致重复推理，每代每图本地只接受一个结果，迟到的重复答案被丢弃。

## 开关与恢复

`Mistake:Layout:Enabled` 关闭（重启后）：拒绝新作业（`layout_disabled` 503），不再领取已有作业；作业、接受结果与读取永久保留，重新启用后 Queued/退避中的作业按原状态恢复。非空禁止 Down（空 layout 集可 Down 干净移除）；备份必须含五张新表、全部来源相关表与函数/trigger/FK。详见[迁移](../migrations.md)与[部署](../../development/Deployment.md#版面检测作业持久化与安全停用)。
