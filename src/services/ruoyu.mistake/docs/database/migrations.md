# 迁移历史

## 20260514051544_InitialCreate

**日期**: 2026-05-14
**类型**: 初始创建

### 创建的表

1. **mistake_item** — 包含所有字段及 6 个索引
2. **mistake_practice** — 包含外键 CASCADE 约束及 4 个索引
3. **process_log** — 包含外键 CASCADE 约束及 3 个索引

### 启动与显式升级

普通 Host 启动只在只读事务中读取 PostgreSQL catalog 和迁移历史，核对关系、列、类型、空值性、默认值、索引及约束。空库、旧 `public.mistake`、不完整或未知结构及不一致历史都拒绝启动，数据库保持原状。只接受精确第八条迁移后的目标结构；此前所有受支持历史形态均须经显式升级。

创建全新空库后须单独执行 `--upgrade-empty-db`。该命令以 advisory transaction lock 串行化，仅在 public schema 为空时于单个事务中应用迁移并核验目标；目标已存在时可重复执行。其余形态不修改。

受支持的带数据 EF 历史库另用 `--upgrade-known-db`。它要求显式确认目标库名、已停写和可恢复备份，在同一事务的 advisory lock 下核对 public 的关系、列/类型/空值性、默认值、索引、约束、触发器及历史记录。精确 InitialCreate（1 条历史）执行后七条正规迁移；前两条历史执行后六条；前三条历史的 `review_comment` 执行列重命名与第五条迁移；前三条历史但已由旧运行时重命名的 `root_cause`，在目标 catalog 精确核验后补第四条历史并应用第五条迁移；四条历史的库继续应用第五条迁移；五条历史的已知库继续应用第六条；六条历史的已知来源库继续应用第七条；七条历史的已知目标库继续应用第八条；八条历史的目标库仅复核。另一种**精确三历史误盖章形态**也受显式入口支持：`root_cause` 已重命名，`process_log.student_id` 及索引和 `reanalyze_job` 已存在，但 `mistake_practice.image_paths` 仍为 `text`，其余完整指纹与该历史运行时一致。入口先锁定练习表并核验每行均为 JSON 字符串数组，保存 ID 与路径序列清单，在同一事务中用显式 `USING image_paths::jsonb` 转换、复核每行语义与完整目标结构，再补第四历史并应用第五条迁移。空数组、中文、大小写、顺序与重复字符串保留；无效 JSON 或非字符串数组拒绝并回滚，不打印原始路径。迁移及最终核验成功后才提交，重复执行目标状态不写入。两个显式命令复用同一锁。普通启动仍只读，升级前历史形态也必须先完成显式升级。

同一三历史 text 路径形态还有一个受支持的精确变体：`process_log.student_id` 索引名为小写 `ix_process_log_student_id`，完整定义必须是 `CREATE INDEX ix_process_log_student_id ON public.process_log USING btree (student_id)`，其他 catalog 和历史完全一致。该变体仅在显式升级事务中先将索引改名为 `IX_process_log_student_id`，再执行上述路径转换与历史/租约升级；失败或取消连同改名一起回滚。大小写双索引、缺失索引、表达式、部分索引、唯一索引、INCLUDE、其他访问方法均拒绝；不全局忽略索引名大小写。普通启动仍只读拒绝，空库入口也不接受此变体。

此入口仅支持仓库迁移生成的上述精确形态及上述两种完整识别的误盖章索引变体。`public.mistake`、历史表独存、部分对象、额外对象、其他误盖章或历史/结构矛盾一律拒绝，不能仅以 `student_id` 列存在推断第二条迁移已完成。PostgreSQL 15 隔离备份恢复校验见[演练说明](../development/BackupRestoreDrill.md)；旧表数据映射、未知/混合结构仍由父项 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 处理。操作和故障恢复见[部署说明](../development/Deployment.md)。

### 列重命名

| 旧列名 | 新列名 | 所在表 |
|--------|--------|--------|
| `status` | `review_status` | mistake_item |
| `mistake_id` | `mistake_item_id` | process_log, mistake_practice |

### 新增列

| 列名 | 类型 | 所在表 |
|------|------|--------|
| `source_upload_id` | `VARCHAR(64)` | mistake_item |
| `source_regions` | `JSONB` | mistake_item |
| `reviewer_id` | `VARCHAR(36)` | mistake_item |
| `reviewed_at` | `TIMESTAMPTZ` | mistake_item |
| `review_comment` | `TEXT` | mistake_item |
| `type` | `INT` | mistake_item |
| `question_id` | `VARCHAR(36)` | mistake_item |

旧表中的 `date`、`image_paths`、`fingerprint` 等字段由本保护原样保留；不可把启动拒绝当成已完成的数据迁移。

## 20260612030015_AddStudentIdToProcessLog

**日期**: 2026-06-12
**类型**: 增量迁移

### 变更内容

1. **process_log** — 新增 `student_id` 列 (`VARCHAR(36)`, NULL) 及索引 `IX_process_log_student_id`
2. **mistake_practice** — `image_paths` 列类型从 `text` 改为 `jsonb`

对于尚未应用本迁移的正规 EF 历史链，类型变更显式使用 `USING image_paths::jsonb`；回退到 InitialCreate 使用 `USING image_paths::text`。仅承诺 JSON 数组的路径字符串语义保留，不承诺 JSON 文本空白字节一致。已经真实应用本迁移的数据库不会重执行。非法 JSON 会使显式升级事务失败并回滚新增列、索引和历史记录，原始文本保持不变。遇到失败或不一致结构，应停止写入、保留备份与诊断；不要用 `EnsureCreated`、手工添加 `student_id` 或手工 stamp 绕过拒绝。

### 历史兼容边界

旧版本可能在启动时直接添加 `student_id` 并错误地只记录 InitialCreate。此类结构与迁移历史不一致，新的只读启动与显式升级均拒绝，且不会自动 stamp。先备份并核验实际列、`image_paths` 类型和历史，再按 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK) 的后续切片处理；不要手工补写历史记录。

### 新增列

| 列名 | 类型 | 所在表 |
|------|------|--------|
| `student_id` | `VARCHAR(36)` NULL | process_log |

### 类型变更

| 列名 | 旧类型 | 新类型 | 所在表 |
|------|--------|--------|--------|
| `image_paths` | `text` | `jsonb` | mistake_practice |

## 20260929000001_RenameReviewCommentToRootCause

**类型**：版本化列重命名。新建空库及受支持带数据历史库按迁移链将 `mistake_item.review_comment` 重命名为 `root_cause`，并写入第四条 EF 历史记录。此前运行时已重命名且只有前三条历史记录的已知结构须经显式升级：精确核验后补第四条历史并应用第五条迁移，不重复重命名。普通启动不补写历史。目标字段为纯文本，保存错题根因。

## 相关文档

- [表结构文档](./tables/)
- [实体关系](./relations.md)
- [总览](./README.md)

## 20260930000001_LeaseReanalyzeJobs

第五条版本化迁移在 `reanalyze_job` 添加 `owner_id`、`lease_until`、`fencing_version`、`attempt`、`execution_root_cause`、`execution_input_digest`。旧无租约 Running 在同一事务转 Failed，原因码为 `upgrade_interrupted`；关联 Item 存在时写一条 jobId 日志。Pending 与已有终态不变。前置条件是所有旧 worker 和写入均已停止、精确库名及可恢复备份已核验。正常启动只接受八历史目标；未知形态拒绝且不写。升级与回退操作见[部署说明](../development/Deployment.md)。

## 20261002120000_AddSourceIntake

第六条迁移新增[来源入库数据](./tables/source_intake.md)及同来源序列化、immutable/result完整性 guards。五历史目标现在是已知可升级状态；此前正规历史及支持的 misstamp 先沿已有修复收敛再应用第六条，整个显式升级同事务。来源升级的第六历史继续应用第七、八条；空库/known入口均严格核验八历史完整 catalog，正常启动仍只读，未知 public.mistake/额外对象/坏函数或 trigger 一律拒绝。

允许所有 intake 表无数据时受控 Down；只要来源 intake 非空就禁止 Down 或旧二进制回退，不删除已创建结果或 Student pins。回退只能停新领取并保留新 binary/schema/guards，或停写后按真实备份恢复流程恢复隔离验证过的完整数据库。pg_dump/restore 验证 manifest、模式、fence/lease、item/practice/log/refs、请求 ID 重放及数据库保护；来源提供方数据库和 pin 也须保留。

## 20261003000001_AddSourceTargets

在当前已知六历史（AddSourceIntake）后增加来源目标 object/attempt、restrict 关系、每图唯一映射/每尝试独立键与永久 guards，保留已有 item/practice/log/source/group/replay 的 ID 和数据。普通启动仅接受八历史的精确 catalog；未知 schema、函数/trigger/FK 变化保持失败关闭。目标数据结构见[来源表](./tables/source_intake.md)。非空目标关系 Down 拒绝；必须停写并验证包含来源、候选、选定映射及所有 guards 的可恢复备份后才升级或恢复。两个显式入口复用已有全局升级锁与事务。

## 20261003010000_AddSourceReturns

第八条版本化迁移新增持久 item 操作、按 group 原顺序去重的 object 与每次独立随机键 attempt，显式 CopySettled 与选定证明、固定 callback payload/digest、原历史receipt和原子 Completed/Rejected/单条日志。新增 writer 与 deferred integrity guards 同时保护 item/practice/log/job；事实永久保留、非空 Down 拒绝。数据关系见[来源表](./tables/source_intake.md)，业务与可信回执模型见[SourceReturns](../modules/SourceReturns.md)。

空库与精确 known 1–7（含此前明确支持的 misstamp）在原事务/锁内升级，最后核验完整八历史 catalog；正常启动只读、不修复漂移。恢复必须含 source_return/source_return_object/source_return_attempt、所有既有业务/来源/目标表与函数/trigger/FK。真实 dump/restore 后核对稳定 ID/载荷/选定/lease/阶段，再续同 operation；来源提供方历史receipt、snapshot/pins与对象均须保留。
