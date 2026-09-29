# PostgreSQL 存储集成

Mistake 使用 Npgsql/EF Core 访问独立的 PostgreSQL 数据库。连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 从 Consul `PostgreSql:*` + `Database:Name` 构建，无法构建时使用 `ConnectionStrings:Default`。表、字段和关系的权威说明见[数据库文档](../database/README.md)。

## 启动初始化

Host 在映射 HTTP 端点和 `app.Run()` 之前调用 `DatabaseInitializer.InitializeAsync`。初始化首先通过只读 `to_regclass('public.mistake')` 检查旧关系名。只要 `public.mistake` 被关系占用，无论表是否为空、是否与 `mistake_item` 并存、是否有外键或迁移历史，启动都会抛出固定的可操作错误并停止；检查失败也会中止。拒绝前不执行 DDL、历史 stamp 或 Common initializer。其他 schema 中的同名关系不触发此保护。此保护假设启动期间没有外部 DDL 并发改变该名称。

不存在 `public.mistake` 时，当前初始化路径继续执行 `EnsureColumnRenamesAsync`、已有表的历史 stamp 和 Common initializer。它们的历史库识别及完整恢复仍是[迁移父项](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IK)的工作，本保护不承诺修复那些行为。JSONB 序列化、级联删除与乐观并发由 EF 模型和[表文档](../database/README.md)定义。

## 验证与运维

`LegacySchemaGuardPostgresTests` 在独立 PostgreSQL 数据库验证仅旧表、新旧表并存、空旧表、被外键引用、已有/不存在历史、并发进程、连接失败及其他 schema 同名关系。设置 `MISTAKE_LEGACY_GUARD_TEST_CONNECTION` 为专用 PostgreSQL 管理连接串后，在 `src/services/ruoyu.mistake/src` 运行 `dotnet test Ruoyu.Study.Mistake.sln --configuration Release`。旧 schema 被拒绝时保留数据库及备份，按[部署说明](../development/Deployment.md)的显式升级要求处理；不通过重启旧版本或手动 stamp 绕过保护。
