# source_handoff — 永久来源交接

本域唯一 source/revision 操作，业务语义见[SourceHandoffs](../../modules/SourceHandoffs.md)。

| 字段 / 约束 | 事实 |
|---|---|
| source_upload_id/content_revision UUID 复合PK | RESTRICT FK source_intake，同一来源版本唯一 |
| snapshot_id UUID UNIQUE / operation_id TEXT UNIQUE | 原snapshot和mistake-handoff:{snapshotId:D}身份 |
| student_id/actor TEXT、sealed_at TIMESTAMPTZ | 原manifest学生、可信原封口主体与DB时钟 |
| groups_canonical/groups_digest TEXT | 完整封口group集合及原生SHA256；原group关联永久 |
| targets/payload_canonical/payload_digest TEXT | 固定所有distinct mistake目标map和source-handoff-v1完整身份 |
| owner_id UUID、fence BIGINT | 现source lease owner/fence，无第二租约 |
| phase TEXT | Prepared/Sending/Conflict/Completed |
| receipt TEXT nullable、completed_at TIMESTAMPTZ nullable、error_kind TEXT nullable | 原可信历史receipt、本域完成时间、固定错误原因 |

INSERT/UPDATE guard重新核验完整真实item/log/终态或return.Completed、全manifest路径和Verified目标、固定group/payload编码、当前有效source fence。固定身份/actor/time/groups/targets/canonical不能改，Completed/Conflict不复活，DELETE/TRUNCATE禁止。Completed只在receipt全原字段/UTC/结果元组匹配时保存；SQL不能提交不完整final。

M9同时替换source/group/request/item最终guards：封口后不增组/新pending物化/状态回退；完整旧请求或同group alias可重放；新revision只接受全部旧handoff.Completed和同历史student，legacy item保持冲突。版本8仍按原SourceReturns catalog严格验证，不跳过旧guards。

非空禁止Down；备份必须含此表、全部既有source/group/request/targets/returns/item/log与函数/trigger/FK，恢复保留原状态/ID/载荷和对象pins。详见[迁移](../migrations.md)及[部署](../../development/Deployment.md#来源交接启用与恢复)。
