using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.Mistake.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(MistakeDbContext context, ILogger logger)
    {
        var pendingMigrations = (await context.Database.GetPendingMigrationsAsync()).ToList();

        if (!pendingMigrations.Any())
        {
            logger.LogInformation("数据库已是最新状态，无需迁移");
            return;
        }

        var canConnect = await context.Database.CanConnectAsync();
        if (!canConnect)
        {
            logger.LogInformation("数据库不存在，将通过迁移自动创建");
            await context.Database.MigrateAsync();
            logger.LogInformation("数据库创建并迁移完成");
            return;
        }

        var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToList();

        if (appliedMigrations.Count == 0)
        {
            logger.LogInformation("检测到遗留数据库（由 EnsureCreated 创建），正在迁移到 Migrations 模式");

            await context.Database.OpenConnectionAsync();
            try
            {
                await using var cmd = context.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                        ""MigrationId"" character varying(150) NOT NULL,
                        ""ProductVersion"" character varying(32) NOT NULL,
                        CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                    )";
                await cmd.ExecuteNonQueryAsync();

                var productVersion = typeof(DbContext).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion ?? "8.0.4";

                foreach (var migration in pendingMigrations)
                {
                    await using var insertCmd = context.Database.GetDbConnection().CreateCommand();
                    insertCmd.CommandText = @"
                        INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES (@MigrationId, @ProductVersion)";

                    var migrationIdParam = insertCmd.CreateParameter();
                    migrationIdParam.ParameterName = "@MigrationId";
                    migrationIdParam.Value = migration;
                    insertCmd.Parameters.Add(migrationIdParam);

                    var versionParam = insertCmd.CreateParameter();
                    versionParam.ParameterName = "@ProductVersion";
                    versionParam.Value = productVersion;
                    insertCmd.Parameters.Add(versionParam);

                    await insertCmd.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }

            logger.LogInformation("遗留数据库已标记所有迁移为已应用");
        }
        else
        {
            logger.LogInformation("发现 {Count} 个待应用的数据库迁移：{Migrations}",
                pendingMigrations.Count, string.Join(", ", pendingMigrations));
            await context.Database.MigrateAsync();
            logger.LogInformation("数据库迁移完成");
        }
    }
}
