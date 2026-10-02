# Mistake 数据库备份恢复演练

本演练仅验证仓库迁移产生的精确 EF 历史链、受支持的三历史 text 路径误盖章形态（标准与精确小写索引变体）与当前目标结构，使用 PostgreSQL 15 和合成数据，不接触线上库。自动化入口是 `BackupRestorePostgresTests`：它在**专用** PostgreSQL 15 容器中创建随机命名的源库与全新恢复库，通过同容器的 `pg_dump -Fc` 和 `pg_restore --single-transaction --exit-on-error` 执行真实备份恢复。测试结束只删除本次创建的库和临时 dump；失败诊断只含阶段和退出码，不打印连接串、口令或业务内容。测试库及其数据均须视为可丢弃。

## 自动化运行

启动一只可丢弃的 `postgres:15` 容器并将 5432 映射到本机回环地址。设置下列变量后，在 `src/services/ruoyu.mistake/src` 运行：

```bash
export MISTAKE_BACKUP_TEST_CONNECTION='Host=127.0.0.1;Port=<映射端口>;Database=postgres;Username=<管理用户>;Password=<密码>'
export MISTAKE_BACKUP_TEST_PG_CONTAINER='<容器名>'
dotnet test Ruoyu.Study.Mistake.sln --configuration Release --filter FullyQualifiedName~BackupRestorePostgresTests
```

连接必须能创建/删除数据库，且容器内该用户可通过 `127.0.0.1:5432` 访问同一 PostgreSQL 15 实例。不得指向共享、测试以外或生产实例。缺任一变量时专项测试跳过，跳过不等于通过。凭据只在当前进程环境传给 Npgsql 和容器客户端，不进入仓库、文件名或测试输出；运行后清除环境变量。并行实例使用不同随机库名和临时目录。

测试对 InitialCreate 带数据形态、三历史 text 路径误盖章形态的两种索引命名变体与当前八历史目标形态分别取一致性 custom-format dump，在全新库恢复，比较 public 列、类型、空值性、索引、约束、迁移历史及全部实际业务表的行内容散列、行数和稳定 ID。fixture 含中文、大小写敏感图片路径、JSON 数组与空数组、空值、关联行；InitialCreate 恢复后执行 `UpgradeKnownAsync` 并再次核对当前八历史目标、业务行与引用。目标恢复后执行只读启动核验；同一 dump 可重复恢复到另一个空库。截断 dump、预取消、只读权限、错误目标及失败后新库重试不产生通过标记，源库前后清单保持一致。清单仅在测试内存中保存非明文散列和合成 ID，不留真实业务明文或凭据。

## 实际环境的操作清单

1. 停止 Mistake 全部写入，核实 PostgreSQL 主机、库名、版本、容量、备份保管位置与维护账号权限；确认是精确受支持的 EF 历史形态或受支持的三历史 text 路径形态（含精确小写索引变体）。旧 `public.mistake`、其他误盖章及未知/混合结构直接停止，交 [IKJ0IK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK)；不得删表或手工写迁移历史。
2. 用 PostgreSQL **15** 客户端对停写的源库作 custom-format dump，记录源库结构、四张业务表中实际存在的表及 `__EFMigrationsHistory` 的行数、稳定 ID、外键、关键索引、JSONB 路径与内容。InitialCreate 的 `mistake_practice.image_paths` 是 text，升级后才是 jsonb。清单只存受保护位置；不要把备份文件、凭据或业务明文提交到仓库。
3. 新建一只**不同名称的空测试库**，向该库执行 `pg_restore --single-transaction --exit-on-error`。检查退出状态后，再独立查询恢复库的结构、历史、行数、ID、引用及关键内容，与源库清单逐项比较。仅 dump 文件存在、`pg_restore` 成功或表数一致都不足以确认可恢复。若有失败、取消、容量不足或权限错误，标记该目标库不可用，保留无敏感内容的诊断，另建空库重试；不得覆盖源库。
4. 恢复库匹配后，才可把**该次备份**记为已验证。受支持的旧 EF 与精确 text 路径形态可在隔离恢复库用精确目标库名、停写与已验证备份确认运行 `--upgrade-known-db`，随后核对八条历史、全部业务/source/group/request/target object/attempt/return operation/object/attempt 表、行/ID/引用和函数/trigger/FK保护及只读启动。对生产库是否升级仍需操作者独立确认同一源库备份和当前停写状态；测试结果不能自动授权生产操作。
5. 若升级后行为异常或提交结果不明，继续停写，先查询当前库实际 schema、历史和业务行；从原备份**再建新库**恢复并校验，再决定实际恢复或重试。不要把 EF Down 当作生产恢复保证。

适用范围及显式命令详见[部署说明](./Deployment.md)与[迁移历史](../database/migrations.md)。本演练不证明旧 `public.mistake` 的数据映射，也不证明生产灾备能力。

受管退回阶段恢复另由 `SourceReturnRecoveryPostgresTests.RealDumpRestoreAllReturnStatesAndCandidatesRetainsFactsAndResumesAsync` 通过实际 PG dump/restore 校验 Prepared、未知 Copy、持久 CopySettled、Sending、Conflict、Completed 的操作/候选完整行。恢复后正常启动只读核验，非 Conflict 同 operation 恢复；Conflict 永久保留且不重复发布。该组沿[SourceReturns](../modules/SourceReturns.md)的 owned fixture 配置运行，不能省略 Student 历史receipt和来源 pins。
