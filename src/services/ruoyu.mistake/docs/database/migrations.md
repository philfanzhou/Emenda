# 迁移历史

## 20260514051544_InitialCreate

**日期**: 2026-05-14
**类型**: 初始创建

### 创建的表

1. **mistake_item** — 包含所有字段及 6 个索引
2. **mistake_practice** — 包含外键 CASCADE 约束及 4 个索引
3. **process_log** — 包含外键 CASCADE 约束及 3 个索引

### 启动时自动升级

Host 正常启动时自动把数据库升级到当前目标结构（十条 EF 历史，最后一条为 `20261003030001_AddStorageReferenceSnapshots`），不需要人工命令、停写确认或备份前置。整个过程在一个 `READ COMMITTED` 事务内：先取 advisory transaction lock，再读取 PostgreSQL catalog 与迁移历史，核对关系、列、类型、空值性、默认值、索引、约束、函数、触发器及历史记录，按下表处理，最后整体核验目标结构后提交。

| 启动时数据库状态 | 结果 |
|---|---|
| 空库（public 下无对象） | 应用全部迁移并核验，继续启动 |
| 已知 1–9 条历史，含下述两种三历史误盖章形态和旧运行时已改名的 `root_cause` | 按历史升级到目标，条目、练习、日志、重分析任务及来源相关表的行与 ID 保留，继续启动 |
| 已是目标（10 条历史） | 不写入，继续启动；只读账号也可启动 |
| 旧表 `public.mistake`、旧版自动维护留下的漂移结构、其他未知或矛盾结构 | 拒绝启动，数据库原样保留 |

升级失败、取消、连接中断或权限不足时整个事务回滚，数据库保持升级前状态，启动失败；诊断只给出版本、本服务自身的核验结论或 SQLSTATE，不输出连接串、密码或业务行内容。下次启动重新分类再试；COMMIT 确认丢失时，下次启动看到已提交的目标结构直接启动。多个实例同时启动时共用同一把锁：一个升级，其余等锁后看到目标结构直接启动，不重复写历史。日志记录升级前版本、执行步骤和目标版本。

已知历史的升级路径：InitialCreate（1 条）、前两条历史及前三条历史的 `review_comment` 先迁移到第四条（列重命名），再迁移到目标；前三条历史但已由旧运行时重命名为 `root_cause` 的库，在 catalog 精确核验后补第四条历史再迁移到目标；4–9 条历史直接迁移到目标。

**精确三历史误盖章形态**：`root_cause` 已重命名，`process_log.student_id` 及索引和 `reanalyze_job` 已存在，但 `mistake_practice.image_paths` 仍为 `text`，其余完整指纹与该历史运行时一致。升级先锁定练习表并核验每行均为 JSON 字符串数组，保存 ID 与路径序列清单，在同一事务中用 `USING image_paths::jsonb` 转换、复核每行语义，再补第四条历史并迁移到目标。空数组、中文、大小写、顺序与重复字符串保留；无效 JSON 或非字符串数组使升级失败并回滚，不打印原始路径。

同一形态还有一个精确变体：`process_log.student_id` 索引名为小写 `ix_process_log_student_id`，完整定义必须是 `CREATE INDEX ix_process_log_student_id ON public.process_log USING btree (student_id)`，其他 catalog 和历史完全一致。升级时先将索引改名为 `IX_process_log_student_id`，再执行上述转换；失败或取消连同改名一起回滚。大小写双索引、缺失索引、表达式、部分索引、唯一索引、INCLUDE、其他访问方法均拒绝；不全局忽略索引名大小写。

旧库中同一学生、同一来源、相同区域的重复条目照常升级，所有行保留；现行设计不对旧行加唯一约束，对该来源的新受管入库仍返回 `legacy_source_conflict`。

自动升级只支持上述形态。`public.mistake`、历史表独存、部分对象、额外对象、其他误盖章、`ProductVersion` 不一致或历史/结构矛盾一律拒绝，不能仅以 `student_id` 列存在推断第二条迁移已完成。旧版自动维护留下的结构由 [IKK6UF](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKK6UF) 处理，`public.mistake` 旧表由 [IKK6UK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKK6UK) 处理。

`--upgrade-empty-db` 与 `--upgrade-known-db` 两个命令为兼容现有脚本保留：执行与启动相同的升级后退出 0。`--writes-stopped`、`--backup-verified` 仍可传但不再必需；显式传了 `--database` 且与实际库名不同则拒绝。备份恢复演练是建议做法，不是升级前置，见[演练说明](../development/BackupRestoreDrill.md)。操作和故障恢复见[部署说明](../development/Deployment.md)。

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

对于尚未应用本迁移的正规 EF 历史链，类型变更显式使用 `USING image_paths::jsonb`；回退到 InitialCreate 使用 `USING image_paths::text`。仅承诺 JSON 数组的路径字符串语义保留，不承诺 JSON 文本空白字节一致。已经真实应用本迁移的数据库不会重执行。非法 JSON 会使启动时的升级事务失败并回滚新增列、索引和历史记录，原始文本保持不变。遇到失败或不一致结构，保留诊断；不要用 `EnsureCreated`、手工添加 `student_id` 或手工 stamp 绕过拒绝。

### 历史兼容边界

旧版本可能在启动时直接添加 `student_id` 并错误地只记录 InitialCreate。此类结构与迁移历史不一致，当前启动拒绝且不会自动 stamp，由 [IKK6UF](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKK6UF) 处理；不要手工补写历史记录。

### 新增列

| 列名 | 类型 | 所在表 |
|------|------|--------|
| `student_id` | `VARCHAR(36)` NULL | process_log |

### 类型变更

| 列名 | 旧类型 | 新类型 | 所在表 |
|------|--------|--------|--------|
| `image_paths` | `text` | `jsonb` | mistake_practice |

## 20260929000001_RenameReviewCommentToRootCause

**类型**：版本化列重命名。新建空库及受支持带数据历史库按迁移链将 `mistake_item.review_comment` 重命名为 `root_cause`，并写入第四条 EF 历史记录。此前运行时已重命名且只有前三条历史记录的已知结构在启动时精确核验后补第四条历史再迁移到目标，不重复重命名。目标字段为纯文本，保存错题根因。

## 相关文档

- [表结构文档](./tables/)
- [实体关系](./relations.md)
- [总览](./README.md)

## 20260930000001_LeaseReanalyzeJobs

第五条版本化迁移在 `reanalyze_job` 添加 `owner_id`、`lease_until`、`fencing_version`、`attempt`、`execution_root_cause`、`execution_input_digest`。旧无租约 Running 在同一事务转 Failed，原因码为 `upgrade_interrupted`；关联 Item 存在时写一条 jobId 日志。Pending 与已有终态不变。升级前应先停掉旧实例（单实例替换即可满足），否则旧 worker 仍可能写入；这不是启动校验项。未知形态拒绝且不写。升级与回退操作见[部署说明](../development/Deployment.md)。

## 20261002120000_AddSourceIntake

第六条迁移新增[来源入库数据](./tables/source_intake.md)及同来源序列化、immutable/result完整性 guards。五历史目标现在是已知可升级状态；此前正规历史及支持的 misstamp 先沿已有修复收敛再继续迁移，整个升级同事务，最后严格核验目标完整 catalog；未知 public.mistake/额外对象/坏函数或 trigger 一律拒绝。

允许所有 intake 表无数据时受控 Down；只要来源 intake 非空就禁止 Down 或旧二进制回退，不删除已创建结果或 Student pins。回退只能停新领取并保留新 binary/schema/guards，或停写后按真实备份恢复流程恢复隔离验证过的完整数据库。pg_dump/restore 验证 manifest、模式、fence/lease、item/practice/log/refs、请求 ID 重放及数据库保护；来源提供方数据库和 pin 也须保留。

## 20261003000001_AddSourceTargets

在当前已知六历史（AddSourceIntake）后增加来源目标 object/attempt、restrict 关系、每图唯一映射/每尝试独立键与永久 guards，保留已有 item/practice/log/source/group/replay 的 ID 和数据。启动时升级后核验目标精确 catalog；未知 schema、函数/trigger/FK 变化保持失败关闭。目标数据结构见[来源表](./tables/source_intake.md)。非空目标关系 Down 拒绝；恢复须使用包含来源、候选、选定映射及所有 guards 的完整备份。启动升级与两个兼容命令复用同一全局升级锁与事务。

## 20261003010000_AddSourceReturns

第八条版本化迁移新增持久 item 操作、按 group 原顺序去重的 object 与每次独立随机键 attempt，显式 CopySettled 与选定证明、固定 callback payload/digest、原历史receipt和原子 Completed/Rejected/单条日志。新增 writer 与 deferred integrity guards 同时保护 item/practice/log/job；事实永久保留、非空 Down 拒绝。数据关系见[来源表](./tables/source_intake.md)，业务与可信回执模型见[SourceReturns](../modules/SourceReturns.md)。

空库与精确 known 1–8（含此前明确支持的 misstamp）在启动时同一事务/锁内升级，最后核验完整目标 catalog；不修复漂移。恢复必须含 source_return/source_return_object/source_return_attempt、所有既有业务/来源/目标表与函数/trigger/FK。真实 dump/restore 后核对稳定 ID/载荷/选定/lease/阶段，再续同 operation；来源提供方历史receipt、snapshot/pins与对象均须保留。

## 20261003020001_AddSourceHandoffs

第九条正规迁移新增[source_handoff](./tables/source_handoff.md)固定完整group/target/payload与原历史receipt；替换source/newrevision、group/seal、request/pending materialization、item/terminal和handoff最终guards。known1–8保持精确catalog识别，version8仍验证原returns对象/完整函数和guards；启动时不修复漂移，也不宽松stamp。未知/缺失/额外表、函数、trigger、FK/history原状拒绝。

空库与已知历史在启动时同一advisory事务内升级；原rows/requests/targets/return candidates/IDs不可改。非空handoff禁止Down/旧binary回退；恢复完整全部业务/来源/目标/退回/交接表与guards，核对原op/state/receipt后同fixedpayload继续。两服务与原对象桶一并保留。
