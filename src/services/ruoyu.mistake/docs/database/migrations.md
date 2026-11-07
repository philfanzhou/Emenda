# 迁移历史

## 20260514051544_InitialCreate

**日期**: 2026-05-14
**类型**: 初始创建

### 创建的表

1. **mistake_item** — 包含所有字段及 6 个索引
2. **mistake_practice** — 包含外键 CASCADE 约束及 4 个索引
3. **process_log** — 包含外键 CASCADE 约束及 3 个索引

### 启动时自动升级

Host 正常启动时自动把数据库升级到当前目标结构（十四条 EF 历史，最后一条为 `20261107000001_DropStorageReferenceSnapshots`），不需要人工命令、停写确认或备份前置。整个过程在一个 `READ COMMITTED` 事务内：先取 advisory transaction lock，再读取 PostgreSQL catalog 与迁移历史，核对关系、列、类型、空值性、默认值、索引、约束、函数、触发器及历史记录，按下表处理，最后整体核验目标结构后提交。

| 启动时数据库状态 | 结果 |
|---|---|
| 空库（public 下无对象） | 应用全部迁移并核验，继续启动 |
| 已知 1–13 条历史，含下述两种三历史误盖章形态和旧运行时已改名的 `root_cause` | 按历史升级到目标，条目、练习、日志、重分析任务及来源相关表的行与 ID 保留，继续启动 |
| 2026-05-14 至 2026-09-29 版本自动维护留下的旧结构（见[旧版自动维护结构的收敛](#旧版自动维护结构的收敛)） | 同一事务内先收敛到已知形态，被移除或改写的旧数据写入 `legacy_archive`，再升级到目标，继续启动 |
| 已是精确目标（14 条历史及完整结构） | 不写入，继续启动；只读账号也可启动 |
| 2026-04-19 至 2026-05-13 版本留下的精确 `public.mistake`（见[旧表转换](#旧表-mistake-的转换)） | 保留旧条目与关系，同事务转换、归档并升级，继续启动 |
| 其他未知或矛盾结构 | 拒绝启动，数据库原样保留 |

空库或已知旧版本升级需要相应的 DDL 与迁移历史写入权限；旧结构收敛还需要其数据写入权限。13 条历史的库仍须删除 StorageReferences 快照表并写入第十四条历史，不能以只读账号完成启动。只有历史和完整结构均精确符合版本 14 时，启动才保持零写入。

For the recognized fourteen-migration chain, `ProductVersion` accepts the original `8.0.11` stamp and the current EF Core `10.0.13` stamp, including a mixture after upgrading an existing database. Existing stamps are preserved; only newly applied migrations receive the current EF version. Other versions remain unsupported, and the complete schema checks still apply. The EF 10 model snapshots introduce no new migration or database schema change. The provider-defined history table is created inside the existing advisory-lock transaction before EF 10 probes applied migrations; rollback and final catalog verification still cover the whole upgrade.

升级事务内的命令（含等待 advisory lock）不受连接串 `Command Timeout`（Npgsql 默认 30 秒）限制，大库首次升级不会因命令超时回滚；宿主停止或取消仍会中断并回滚。没有新增配置项。

升级失败、取消、连接中断或权限不足时整个事务回滚，数据库保持升级前状态，启动失败；诊断只给出版本、本服务自身的核验结论或 SQLSTATE，不输出连接串、密码或业务行内容。下次启动重新分类再试；COMMIT 确认丢失时，下次启动看到已提交的目标结构直接启动。多个实例同时启动时共用同一把锁：一个升级，其余等锁后看到目标结构直接启动，不重复写历史。日志记录升级前版本、执行步骤和目标版本。

已知历史的升级路径：InitialCreate（1 条）、前两条历史及前三条历史的 `review_comment` 先迁移到第四条（列重命名），再迁移到目标；前三条历史但已由旧运行时重命名为 `root_cause` 的库，在 catalog 精确核验后补第四条历史再迁移到目标；4–13 条历史直接迁移到目标版本 14。

**精确三历史误盖章形态**：`root_cause` 已重命名，`process_log.student_id` 及索引和 `reanalyze_job` 已存在，但 `mistake_practice.image_paths` 仍为 `text`，其余完整指纹与该历史运行时一致。升级先锁定练习表并核验每行均为 JSON 字符串数组，保存 ID 与路径序列清单，在同一事务中用 `USING image_paths::jsonb` 转换、复核每行语义，再补第四条历史并迁移到目标。空数组、中文、大小写、顺序与重复字符串保留。

**非法练习路径**：`image_paths` 仍为 `text` 的库（InitialCreate 一条历史、上述误盖章形态及收敛后的旧结构）在转换前逐行校验；不是 JSON 字符串数组的行，原文先写入 `legacy_archive`（`kind=invalid_value`），该行改为 `[]`，日志给出条数，不再拒绝启动，也不打印原始路径。

同一形态还有一个精确变体：`process_log.student_id` 索引名为小写 `ix_process_log_student_id`，完整定义必须是 `CREATE INDEX ix_process_log_student_id ON public.process_log USING btree (student_id)`，其他 catalog 和历史完全一致。升级时先将索引改名为 `IX_process_log_student_id`，再执行上述转换；失败或取消连同改名一起回滚。大小写双索引、缺失索引、表达式、部分索引、唯一索引、INCLUDE、其他访问方法均拒绝；不全局忽略索引名大小写。

旧库中同一学生、同一来源、相同区域的重复条目照常升级，所有行保留；现行设计不对旧行加唯一约束，对该来源的新受管入库仍返回 `legacy_source_conflict`。

自动升级只支持上述形态。不受支持的旧表、历史表独存、部分对象、额外对象、其他误盖章、不受支持的 `ProductVersion` 或历史/结构矛盾一律拒绝，不能仅以 `student_id` 列存在推断第二条迁移已完成。旧表转换的精确边界见下节。

### 旧表 `mistake` 的转换

仍保有 `public.mistake` 的 2026-04-19 至 2026-05-13 旧库，在外层升级事务和 advisory lock 内先只读识别；识别发生在任何表锁、DDL 和数据写入之前。旧表须完整符合 0501 的十列类型/空值性、主键及六个非唯一 btree 索引，练习、日志、明细和可选指纹表须符合 0501 列集。指向旧表的外键只允许历史四种已验证的 `mistake_id → mistake(id)` CASCADE 约束；旧表没有额外索引、约束、触发器、规则、策略或默认值。陌生结构原状拒绝，不进行转换。获取表锁后再次核验，随后先暂存旧条目整行。

| 形态 | 历史与结构 | 结果 |
|---|---|---|
| P0 / P0f | 没有历史表；0501 无指纹表 / 含完整指纹表 | 创建标准历史表，转换并升级 |
| P1 / P1x | 只有 0501 历史；0501 含指纹表 / 缺指纹表 | 转换并升级 |
| P2 / P2f | 0501、0503 历史；0503 无指纹表 / 仍含指纹表 | 转换并升级 |
| M-da / M-05 / M-e0 | P2 或 P2f 加 05-10 至 05-13 三种 `mistake_item` 布局，两表并存 | 先去除现表旧列，再转换；现表已有 ID 优先 |

0501 指纹表包含 `IX_mistake_image_fingerprint_oss_path`。旧历史的 `ProductVersion` 为 `8.0.4`；收敛后的正规历史按上文保留 `8.0.11` 或由当前 EF 写入 `10.0.13`。

没有 `mistake_item` 时按 InitialCreate 建立十三列。只移除四种已知的旧外键，把旧列处理提前到插入旧条目之前，再在孤儿判定和新外键之前写入旧条目：ID、学生、学科、年级、题目 ID、创建/更新时间保留，`status → review_status`，`source_upload_id=''`、`source_regions=[]`，其他审核字段为空。同 ID 已存在时保留现条目，旧整行按 `legacy_mistake_superseded` 归档；新增条目的旧整行按 `legacy_mistake_converted` 归档。转换合成的值不归档。旧练习和日志 ID、父子关系保留；旧日志动作 1/2 保留，3/4 分别改为 5/6，改前原值按 `legacy_action_renumbered` 归档。

明细图片按下节回填，现条目已有区域不覆盖；每条明细只有实际影响 UPDATE 的那行标为已回填。明细与指纹整行归档后删除，最后删除已经没有依赖的 `mistake`（不使用 CASCADE），沿现有收敛和正规迁移链到十二条历史并整体核验。归档条数为旧条目数 + 改号日志数 + 下节既有归档规则的条数，例如 P2（两条旧条目、一条明细、动作 1/3/4 各一条）为历史 2 + 旧条目 2 + 明细 1 + 改号 2 = 7。

重复启动零写入，并发启动只转换、归档一次。SQL 失败、核验失败、取消、断连、只读或无 DDL 权限均整体回滚，旧表及其数据、索引、约束、历史原样；诊断不含行内容或连接串。归档按业务数据保护，不通过 HTTP 暴露。转换后与普通目标库使用同一结构，无新增迁移；归档非空不能 Down 到第十条。

转换条目没有来源记录，不进入受管入库或受管退回，教师退回走既有旧退回流程，见[来源退回](../modules/SourceReturns.md)；该流程的恢复保证由 [IKIZY3](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZY3) 决定。已经由 05-14 及以后版本转换过的库中，旧、新编号可能混在一起，动作不再改号。04-19 之前模型、旧代码已经删除的数据及旧实例仍写入时的滚动升级不在保证范围。首次转换需 DDL 与写历史权限，大库启动等待按[部署说明](../development/Deployment.md)放宽。

### 旧版自动维护结构的收敛

2026-05-14 至 2026-09-29 的版本在启动时直接执行 DDL 维护数据库，留下与正规迁移不一致的结构。现在启动时在同一事务和锁内、分类之前识别并收敛这些结构，旧数据保留可用。

**进入条件**：旧表已通过上节识别并进入转换，或没有 `public.mistake`；迁移历史只含 `20260501042151_InitialCreate`、`20260503021905_RemoveImageFingerprint`、`20260514051544_InitialCreate`、`20260612030015_AddStudentIdToProcessLog`、`20260626000000_AddReanalyzeJob` 中的记录；并出现至少一个旧代码标志：0501/0503 历史、`mistake_detail`、`mistake_image_fingerprint`、`PK_mistake`/`pk_mistake_item`、小写 `fk_*` 外键、小写 `ix_mistake_item_*` 或 `IX_mistake_*` 旧索引名、`IX_mistake_practice_mistake_id`、`IX_process_log_mistake_id_created_at`、`mistake_item` 上的旧列、`mistake_practice.mistake_id`/`process_log.mistake_id`。不满足时不做任何收敛，已在目标的库仍零写入。

**收敛规则**（先暂存、后改动）：

| 对象 | 处理 |
|---|---|
| 0501/0503 历史行 | 整行归档后删除 |
| `mistake_practice.mistake_id`、`process_log.mistake_id` | 改名为 `mistake_item_id` |
| 父条目已不存在的练习/日志行（旧代码 `DROP TABLE mistake CASCADE` 留下） | 整行归档后删除，再补外键 |
| `PK_mistake`、`pk_mistake_item`、小写 `fk_*` | 定义与目标相同时改为目标名；缺外键时补建 |
| `mistake_item.source_regions` 缺失（da97cc6d 布局） | 补列，按 `source_image_path` 与四个 bbox 值（都有值时）折成一个区域 |
| `mistake_item` 旧列 `type`、`source_image_path`、`bbox_x1..y2`、`question_number`、`question_text`、`student_answer`、`ai_confidence` | 每条目的非空列值归档后删列 |
| `source_upload_id`、`source_regions` 的旧默认值 | 删除 |
| `mistake_detail` | 整行归档；`original_images` 为 JSON 字符串数组且条目 `source_regions` 为 `[]` 时写成全图区域 `{"sourceImagePath": ..., "boundingBox": null}`；随后删表 |
| `mistake_image_fingerprint` | 整行归档后删表 |
| 旧索引名 | 目标名缺失时把定义相同的旧名改为目标名；同列重复的旧名删除；仍缺的目标索引补建 |
| 历史只剩 0501/0503（旧代码加外键失败、每次启动停在同一步的库） | 删除旧代码加的 `process_log.student_id`（非空值先归档）及其索引，`root_cause` 改回 `review_comment`，写入 InitialCreate 历史，再由正规迁移链重新执行这两步 |

收敛后交给现有分类器：结构必须与版本 1–4 的精确结构或上面两种误盖章/旧运行时形态之一完全一致，否则整体回滚并拒绝，诊断只给对象名。旧版自动维护形态（首次由 05-14 至 06-24 或 06-24 之后的代码启动的 05-01/05-03 库、含指纹表的 05-01 库、05-14 至 06-24 代码新建的库、05-10 至 05-13 新旧两表并存后的三种 `mistake_item` 布局、加外键失败停在同一步的库）收敛为“三条历史 + text 路径 + 小写索引”形态或版本 1，再沿迁移链升级到目标；用例见 `src/Tests/Ruoyu.Study.Mistake.Service.Tests/LegacySchemaConvergencePostgresTests.cs`。

不在收敛规则内的对象（陌生表、列、索引、约束、函数、触发器，或旧表上附加的陌生索引/约束/触发器，或旧列上的陌生索引/约束）不自动处理，拒绝启动并原样保留数据库。已被旧代码删除的数据（旧自动升级删掉的 `date`、`image_paths`、`fingerprint` 列，05-14 代码 `DROP TABLE mistake CASCADE` 删掉的旧条目）无法找回；孤儿练习/日志行移出业务表、只在归档表。旧实例仍在写入时的滚动升级不保证。

### 归档规则

被移除或改写的旧数据在同一事务内先写入事务临时表，迁移到目标后一次写入 [`legacy_archive`](./tables/legacy_archive.md) 并核对条数；失败则整体回滚。归档行按 `source_table` + `source_key`（行 ID 或 MigrationId）查找：

| `kind` | 内容 | `reason` |
|---|---|---|
| `history_row` | 被删除的历史行 | `legacy_history_removed` |
| `row` | 被删除的整行（旧表、孤儿行） | `legacy_table_dropped`、`legacy_table_dropped_images_backfilled`、`orphan_row_removed` |
| `column_values` | 被删列的非空值，以列名为键 | `legacy_column_dropped` |
| `row` | 转换或被现条目替代的旧 `mistake` 整行 | `legacy_mistake_converted`、`legacy_mistake_superseded` |
| `column_values` | 改号前旧日志的 `{"action": 原值}` | `legacy_action_renumbered` |
| `invalid_value` | 非法 `image_paths` 原文 | `invalid_image_paths_reset` |

并发启动时 advisory lock 保证只收敛和归档一次。业务代码不读归档表。

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

旧版本可能在启动时直接添加 `student_id`。已知的旧形态按[旧版自动维护结构的收敛](#旧版自动维护结构的收敛)在启动时自动处理；只有 InitialCreate 历史、多出 `student_id` 且没有其他旧代码标志的结构不属于已知形态，当前启动拒绝且不会自动 stamp。不要手工补写历史记录。

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

## 20261008000001_AddLegacyArchive

第十一条迁移新增只追加的 [`legacy_archive`](./tables/legacy_archive.md)，承载旧结构收敛时被移除或改写的数据（见[归档规则](#归档规则)）。表上的触发器拒绝 UPDATE/DELETE/TRUNCATE。Down 在归档表为空时删除表和触发器函数，非空时拒绝。收敛过的库回到只认识十条迁移的版本会因历史不匹配拒绝启动，回退须使用包含本迁移的版本。

## 20261010000001_AddLegacyReturns

第十二条迁移完整增加[旧错题退回日志](./tables/legacy_return.md)、条件租约和同条目最后写入/延期完成保护。原来源真实外键和原业务数据不放宽、不迁改；新表不回填旧操作。完整引用快照覆盖新原图与全部候选，保持 v1 合同。空库和精确已知1–11版本同锁同事务升级，坏或部分结构拒绝；已有操作时 Down 拒绝，使用当前 binary/schema 停止推进。整库备份恢复须包含全部新状态、历史和 guards。

## 20261025000000_AddLayoutJobs

第十三条迁移完整增加[版面检测作业数据](./tables/layout_job.md)：`layout_job`（租约/fence/attempt、retained manifest、非秘密处理配置与 credential 引用）、`layout_job_image`（保留字节身份与每图有界进度）、`layout_result`/`layout_result_region`（每代每图唯一接受结果，自包含绑定来源/版本/快照/字节身份/相关旋转/attempt/配置）与 `layout_request`（请求键重放）。结果、区域与重放记录由 `guard_layout_immutable`/`guard_layout_truncate` 触发器保护为只插入事实。数据语义、安全停用与恢复见该表文档与[部署说明](../development/Deployment.md#版面检测作业持久化与安全停用)。

空库和精确已知 1–12 版本同锁同事务升级并核验完整 catalog（含新表/列/索引/RESTRICT FK/函数体/触发器条件）；未知结构拒绝不修。任意 layout 表非空时 Down 拒绝；空 layout 集可 Down 干净移除全部五表与两个保护函数。回退保留认识当前 schema 的 binary 或按完整备份恢复流程处理。

## 20261107000001_DropStorageReferenceSnapshots

第十四条迁移随 [ADR-0030](../../../../../docs/adr/0030-portal-hosted-administration.md) 第 4 步删除 StorageReferences v1：删除 `storage_reference_snapshot` 表与 `guard_storage_reference_snapshot` 函数。快照会过期、不是业务数据，表非空也直接删除。版本 10–13 的精确 schema 检查仍要求该表，版本 14 要求它不存在（`StorageReferenceRemovalSchema.Present`）。Down 重建空表与保护函数；回退到不认识第十四条历史的旧 binary 前须先执行 Down，否则旧版本因历史不匹配拒绝启动。`LegacySchemaConvergencePostgresTests.VersionThirteenDropsTheRetiredReferenceTableAndDowngradeRecreatesIt` 覆盖升级与 Down。
