# PostgreSQL 存储集成

Mistake 使用 Npgsql/EF Core 访问独立的 PostgreSQL 数据库。默认 Platform 模式的连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 从 Consul `PostgreSql:*` + `Database:Name` 构建，无法构建时使用 `ConnectionStrings:Default`；显式 Local 模式仅使用 `ConnectionStrings:MistakeLocal`（详见[部署说明](../development/Deployment.md#配置引导模式)）。表、字段和关系的权威说明见[数据库文档](../database/README.md)。

## 启动核验与空库升级

Host 在映射 HTTP 端点前以只读事务检查 public schema 的关系、列、类型、默认值、索引、约束和 `__EFMigrationsHistory`。仅接受三条 EF 历史且 `root_cause` 已由旧运行时重命名的已知结构，或四条历史的目标结构。空库、`public.mistake` 旧关系、不完整或未知结构及矛盾历史直接拒绝且不写库；其他 schema 的同名表不触发 public schema 检查。核验期间应避免外部并发 DDL。

全新空库需使用 `--upgrade-empty-db` 显式升级；精确受支持的带数据 EF 历史库使用 `--upgrade-known-db`。两个命令都在 advisory lock 和事务中核验目标后提交，目标结构可重复执行。未知结构及旧 `public.mistake` 数据映射仍由[迁移父项](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK)另行处理。操作前提见[部署说明](../development/Deployment.md)。JSONB 序列化和级联删除见[表文档](../database/README.md)。

## 验证与运维

`ReadOnlyStartupPostgresTests` 与 `LegacySchemaGuardPostgresTests` 使用独立 PostgreSQL 测试库覆盖已知结构的只读启动、空库升级、冲突结构、历史一致性、并发、回滚及旧表拒绝。分别设置 `MISTAKE_READONLY_TEST_CONNECTION` 与 `MISTAKE_LEGACY_GUARD_TEST_CONNECTION` 为专用 PostgreSQL 管理连接串后，在 `src/services/ruoyu.mistake/src` 运行 `dotnet test Ruoyu.Study.Mistake.sln --configuration Release`。生产旧库须先备份，不通过旧版本或手工 stamp 绕过检查。
