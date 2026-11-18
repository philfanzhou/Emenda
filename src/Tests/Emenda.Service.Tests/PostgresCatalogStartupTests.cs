using Emenda.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Emenda.Service.Tests;

public sealed class PostgresCatalogStartupTests
{
    [Fact]
    public async Task CurrentSchemaRestartsWithoutRewritingConstraintsOrRowsAsync()
    {
        await using var database = await CreateDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,root_cause,created_at) VALUES ({id},'catalog-student',1,7,'catalog-source','[]'::jsonb,1,'retained',now())");
        var before = await ConstraintsAsync(context);
        var history = (await context.Database.GetAppliedMigrationsAsync()).ToArray();

        var result = await DatabaseInitializer.UpgradeToTargetAsync(context);

        Assert.False(result.Upgraded);
        Assert.Equal(before, await ConstraintsAsync(context));
        Assert.Equal(history, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        var item = await context.MistakeItems.SingleAsync();
        Assert.Equal(id, item.Id);
        Assert.Equal("retained", item.RootCause);
    }

    [Theory]
    [InlineData("ALTER TABLE mistake_item ALTER COLUMN root_cause SET NOT NULL", "column/type/nullability")]
    [InlineData("ALTER TABLE mistake_item ADD CONSTRAINT catalog_extra_check CHECK (grade >= 0)", "constraint")]
    public async Task UnexpectedSchemaStillFailsWithoutRewritingConstraintsAsync(string mutation, string diagnostic)
    {
        await using var database = await CreateDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync(mutation);
        var before = await ConstraintsAsync(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.UpgradeToTargetAsync(context));

        Assert.Contains(diagnostic, error.Message);
        Assert.Equal(before, await ConstraintsAsync(context));
    }

    private static async Task<LocalBootstrapPostgresTests.TestDatabase> CreateDatabaseAsync()
    {
        var connection = Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_READONLY_TEST_CONNECTION to an isolated PostgreSQL test admin connection.");
        return await LocalBootstrapPostgresTests.TestDatabase.CreateAsync(connection);
    }

    private static MistakeDbContext CreateContext(LocalBootstrapPostgresTests.TestDatabase database)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MISTAKE_READONLY_TEST_CONNECTION")!)
            { Database = database.Name }.ConnectionString;
        return new MistakeDbContext(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connection).Options);
    }

    private static Task<string[]> ConstraintsAsync(MistakeDbContext context) => context.Database.SqlQueryRaw<string>("""
        SELECT conrelid::regclass::text || '|' || conname || '|' || contype::text || '|' || pg_get_constraintdef(oid) AS "Value"
        FROM pg_constraint WHERE connamespace='public'::regnamespace ORDER BY conrelid::regclass::text,conname
        """).ToArrayAsync();
}
