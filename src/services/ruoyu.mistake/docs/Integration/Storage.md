# PostgreSQL 存储集成

Mistake 使用 Npgsql/EF Core 访问独立的 PostgreSQL 数据库。默认 Platform 模式的连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 从 Consul `PostgreSql:*` + `Database:Name` 构建，无法构建时使用 `ConnectionStrings:Default`；显式 Local 模式仅使用 `ConnectionStrings:MistakeLocal`（详见[部署说明](../development/Deployment.md#配置引导模式)）。表、字段和关系的权威说明见[数据库文档](../database/README.md)。

## 启动时自动升级

Host 在映射 HTTP 端点和启动后台 worker 前，在一个持有 advisory lock 的事务中检查 public schema 的关系、列、类型、默认值、索引、约束和 `__EFMigrationsHistory`：空库、已知 EF 历史的旧库和旧版自动维护留下的已知结构（先收敛，被移除或改写的数据归档到 `legacy_archive`）升级到目标结构并核验后提交，已是目标时不写入；`public.mistake` 旧关系、不完整或未知结构及矛盾历史直接拒绝且不写库；其他 schema 的同名表不触发 public schema 检查。升级失败整体回滚。核验期间应避免外部并发 DDL。

支持的形态与边界见[迁移历史](../database/migrations.md)。`--upgrade-empty-db` 与 `--upgrade-known-db` 为兼容现有脚本保留，执行同一升级后退出。旧 `public.mistake` 由 [IKK6UK](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKK6UK) 处理。操作见[部署说明](../development/Deployment.md)。JSONB 序列化和级联删除见[表文档](../database/README.md)。

## 验证与运维

`ReadOnlyStartupPostgresTests` 与 `LegacySchemaGuardPostgresTests` 使用独立 PostgreSQL 测试库覆盖空库与已知结构的启动自动升级、目标结构零写入、受限账号、冲突结构、历史一致性、并发、回滚及旧表拒绝。分别设置 `MISTAKE_READONLY_TEST_CONNECTION` 与 `MISTAKE_LEGACY_GUARD_TEST_CONNECTION` 为专用 PostgreSQL 管理连接串后，在 `src/services/ruoyu.mistake/src` 运行 `dotnet test Ruoyu.Study.Mistake.sln --configuration Release`。生产旧库建议升级前保留备份，不通过旧版本或手工 stamp 绕过检查。
