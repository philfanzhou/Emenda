# legacy_archive — 旧结构收敛归档

只追加的归档表，保存启动时收敛旧版自动维护结构所移除或改写的数据（规则见[迁移历史](../migrations.md#归档规则)）。业务代码不读写；只由启动升级在同一事务内写入。

| 字段 | 类型 | 含义 |
|---|---|---|
| `id` | bigint identity，PK `PK_legacy_archive` | 写入顺序 |
| `source_table` | text not null | 原表名，例如 `mistake_detail`、`mistake_practice`、`__EFMigrationsHistory` |
| `source_key` | text not null | 原行标识：行 `id`，历史行为 `MigrationId` |
| `kind` | text not null | `row`、`column_values`、`invalid_value`、`history_row` |
| `payload` | jsonb not null | 整行、被删列的非空值或非法原文 |
| `reason` | text not null | 处理原因码 |
| `archived_at` | timestamptz not null | 收敛事务时间 |

索引 `IX_legacy_archive_source_table_source_key (source_table, source_key)` 用于按来源查找。触发器 `trg_legacy_archive`、`trg_legacy_archive_truncate`（函数 `guard_legacy_archive()`）拒绝 UPDATE、DELETE 和 TRUNCATE。

由迁移 `20261008000001_AddLegacyArchive` 创建；表非空时 Down 拒绝。归档内容可能含旧图片路径和旧备注，按业务数据同等保护，随数据库一起备份。归档中的图片路径不算在用引用；收敛时回填到 `mistake_item.source_regions` 的图片按条目区域计入（[来源核对](../../modules/SourceReconciliation.md)按同一规则读取引用）。

旧表转换新增原因码：`legacy_mistake_converted` 保存写入现表的旧条目整行，`legacy_mistake_superseded` 保存同 ID 已被现条目覆盖的旧整行，`legacy_action_renumbered` 保存改号前日志的 `{"action": 原值}`。转换生成的默认值不归档。`legacy_table_dropped_images_backfilled` 仅标记实际回填图片的明细行；同条目多明细时其余行仍为 `legacy_table_dropped`。完整形态、映射与条数模型见[旧表转换](../migrations.md#旧表-mistake-的转换)。
