using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Database;

namespace Ruoyu.Study.Mistake.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(MistakeDbContext context, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DatabaseInitializer");
        await RejectLegacySchemaAsync(context);
        await context.Database.OpenConnectionAsync();
        try
        {
            await EnsureColumnRenamesAsync(context);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
        await StampMigrationsIfTablesExistAsync(context, logger);
        await Common.Database.DatabaseInitializer.InitializeAsync(context, loggerFactory, GetTableCreationSql);
    }

    private static async Task RejectLegacySchemaAsync(MistakeDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT to_regclass('public.mistake') IS NOT NULL";
            if ((bool)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Legacy schema check returned no result.")))
                throw new InvalidOperationException(
                    "Legacy schema detected: public.mistake exists. Database unchanged. Back up the database and use the explicit upgrade process before starting Mistake.");
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task StampMigrationsIfTablesExistAsync(MistakeDbContext context, Microsoft.Extensions.Logging.ILogger logger)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var checkCmd = context.Database.GetDbConnection().CreateCommand();
            checkCmd.CommandText = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = 'public' 
                    AND table_name = 'mistake_item'
                )";
            var tableExists = (bool)(await checkCmd.ExecuteScalarAsync())!;
            if (!tableExists) return;

            await using var historyCmd = context.Database.GetDbConnection().CreateCommand();
            historyCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" character varying(150) NOT NULL,
                    ""ProductVersion"" character varying(32) NOT NULL,
                    CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                )";
            await historyCmd.ExecuteNonQueryAsync();

            var productVersion = typeof(DbContext).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "8.0.0";

            // Stamp InitialCreate if tables already exist
            await using var checkStampCmd = context.Database.GetDbConnection().CreateCommand();
            checkStampCmd.CommandText = @"
                SELECT COUNT(*) FROM ""__EFMigrationsHistory"" 
                WHERE ""MigrationId"" = '20260514051544_InitialCreate'";
            var count = (long)(await checkStampCmd.ExecuteScalarAsync())!;
            if (count == 0)
            {
                await using var insertCmd = context.Database.GetDbConnection().CreateCommand();
                insertCmd.CommandText = @"
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES (@mid, @pv)";
                var midParam = insertCmd.CreateParameter();
                midParam.ParameterName = "@mid";
                midParam.Value = "20260514051544_InitialCreate";
                insertCmd.Parameters.Add(midParam);
                var pvParam = insertCmd.CreateParameter();
                pvParam.ParameterName = "@pv";
                pvParam.Value = productVersion;
                insertCmd.Parameters.Add(pvParam);
                await insertCmd.ExecuteNonQueryAsync();

                logger.LogInformation("已将 InitialCreate 迁移标记为已应用");
            }

            // Stamp AddStudentIdToProcessLog if student_id column already exists in process_log
            await using var checkStudentIdCmd = context.Database.GetDbConnection().CreateCommand();
            checkStudentIdCmd.CommandText = @"
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public'
                    AND table_name = 'process_log'
                    AND column_name = 'student_id'
                )";
            var studentIdExists = (bool)(await checkStudentIdCmd.ExecuteScalarAsync())!;

            if (studentIdExists)
            {
                await using var checkStamp2Cmd = context.Database.GetDbConnection().CreateCommand();
                checkStamp2Cmd.CommandText = @"
                    SELECT COUNT(*) FROM ""__EFMigrationsHistory""
                    WHERE ""MigrationId"" = '20260612030015_AddStudentIdToProcessLog'";
                var count2 = (long)(await checkStamp2Cmd.ExecuteScalarAsync())!;
                if (count2 == 0)
                {
                    await using var insertStamp2Cmd = context.Database.GetDbConnection().CreateCommand();
                    insertStamp2Cmd.CommandText = @"
                        INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES (@mid, @pv)";
                    var midParam2 = insertStamp2Cmd.CreateParameter();
                    midParam2.ParameterName = "@mid";
                    midParam2.Value = "20260612030015_AddStudentIdToProcessLog";
                    insertStamp2Cmd.Parameters.Add(midParam2);
                    var pvParam2 = insertStamp2Cmd.CreateParameter();
                    pvParam2.ParameterName = "@pv";
                    pvParam2.Value = productVersion;
                    insertStamp2Cmd.Parameters.Add(pvParam2);
                    await insertStamp2Cmd.ExecuteNonQueryAsync();

                    logger.LogInformation("已将 AddStudentIdToProcessLog 迁移标记为已应用");
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureColumnRenamesAsync(MistakeDbContext context)
    {
        // Rename review_comment -> root_cause in mistake_item table
        await using var renameReviewCommentCmd = context.Database.GetDbConnection().CreateCommand();
        renameReviewCommentCmd.CommandText = @"
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'mistake_item' AND column_name = 'review_comment'
                ) THEN
                    ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause;
                END IF;
            END $$;";
        await renameReviewCommentCmd.ExecuteNonQueryAsync();

        await using var checkTablesCmd = context.Database.GetDbConnection().CreateCommand();
        checkTablesCmd.CommandText = @"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'process_log'
            ),
            EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'mistake_practice'
            )";
        using var reader = await checkTablesCmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var processLogExists = reader.GetBoolean(0);
        var practiceExists = reader.GetBoolean(1);
        await reader.CloseAsync();

        if (processLogExists)
        {
            await using var fixProcessLogCmd = context.Database.GetDbConnection().CreateCommand();
            fixProcessLogCmd.CommandText = @"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'process_log' AND column_name = 'mistake_id'
                    ) THEN
                        ALTER TABLE process_log RENAME COLUMN mistake_id TO mistake_item_id;
                    END IF;
                END $$;";
            await fixProcessLogCmd.ExecuteNonQueryAsync();

            await using var addStudentIdCmd = context.Database.GetDbConnection().CreateCommand();
            addStudentIdCmd.CommandText = @"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'process_log' AND column_name = 'student_id'
                    ) THEN
                        ALTER TABLE process_log ADD COLUMN student_id character varying(36) NULL;
                    END IF;
                END $$;";
            await addStudentIdCmd.ExecuteNonQueryAsync();

            await using var addStudentIdIndexCmd = context.Database.GetDbConnection().CreateCommand();
            addStudentIdIndexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS IX_process_log_student_id ON process_log (student_id);";
            await addStudentIdIndexCmd.ExecuteNonQueryAsync();

            await using var addProcessLogFkCmd = context.Database.GetDbConnection().CreateCommand();
            addProcessLogFkCmd.CommandText = @"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE LOWER(conname) = 'fk_process_log_mistake_item_mistake_item_id'
                    ) THEN
                        ALTER TABLE process_log
                            ADD CONSTRAINT FK_process_log_mistake_item_mistake_item_id
                            FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE;
                    END IF;
                END $$;";
            await addProcessLogFkCmd.ExecuteNonQueryAsync();
        }

        if (practiceExists)
        {
            await using var fixPracticeCmd = context.Database.GetDbConnection().CreateCommand();
            fixPracticeCmd.CommandText = @"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'mistake_practice' AND column_name = 'mistake_id'
                    ) THEN
                        ALTER TABLE mistake_practice RENAME COLUMN mistake_id TO mistake_item_id;
                    END IF;
                END $$;";
            await fixPracticeCmd.ExecuteNonQueryAsync();

            await using var addPracticeFkCmd = context.Database.GetDbConnection().CreateCommand();
            addPracticeFkCmd.CommandText = @"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE LOWER(conname) = 'fk_mistake_practice_mistake_item_mistake_item_id'
                    ) THEN
                        ALTER TABLE mistake_practice
                            ADD CONSTRAINT FK_mistake_practice_mistake_item_mistake_item_id
                            FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE;
                    END IF;
                END $$;";
            await addPracticeFkCmd.ExecuteNonQueryAsync();
        }
    }

    private static string? GetTableCreationSql(string tableName)
    {
        return tableName switch
        {
            "mistake_item" => @"
                CREATE TABLE IF NOT EXISTS mistake_item (
                    id uuid NOT NULL,
                    student_id character varying(36) NOT NULL,
                    subject integer NOT NULL,
                    grade integer NOT NULL,
                    source_upload_id character varying(64) NOT NULL,
                    source_regions jsonb NOT NULL DEFAULT '[]',
                    review_status integer NOT NULL,
                    reviewer_id character varying(36) NULL,
                    reviewed_at timestamp with time zone NULL,
                    root_cause text NULL,
                    question_id character varying(36) NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NULL,
                    CONSTRAINT PK_mistake_item PRIMARY KEY (id)
                );
                CREATE INDEX IF NOT EXISTS IX_mistake_item_student_id ON mistake_item (student_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_item_student_id_subject_grade_review_status ON mistake_item (student_id, subject, grade, review_status);
                CREATE INDEX IF NOT EXISTS IX_mistake_item_source_upload_id ON mistake_item (source_upload_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_item_review_status ON mistake_item (review_status);
                CREATE INDEX IF NOT EXISTS IX_mistake_item_created_at ON mistake_item (created_at);",

            "mistake_practice" => @"
                CREATE TABLE IF NOT EXISTS mistake_practice (
                    id uuid NOT NULL,
                    mistake_item_id uuid NOT NULL,
                    student_id character varying(36) NOT NULL,
                    practice_date timestamp with time zone NOT NULL,
                    image_paths text NOT NULL DEFAULT '[]',
                    student_answer text NULL,
                    is_correct boolean NOT NULL,
                    comments text NULL,
                    CONSTRAINT PK_mistake_practice PRIMARY KEY (id),
                    CONSTRAINT FK_mistake_practice_mistake_item_mistake_item_id 
                        FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_mistake_practice_mistake_item_id ON mistake_practice (mistake_item_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_practice_student_id ON mistake_practice (student_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_practice_practice_date ON mistake_practice (practice_date);",

            "process_log" => @"
                CREATE TABLE IF NOT EXISTS process_log (
                    id uuid NOT NULL,
                    mistake_item_id uuid NOT NULL,
                    student_id character varying(36) NULL,
                    action integer NOT NULL,
                    details text NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_process_log PRIMARY KEY (id),
                    CONSTRAINT FK_process_log_mistake_item_mistake_item_id 
                        FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_process_log_mistake_item_id_created_at ON process_log (mistake_item_id, created_at);
                CREATE INDEX IF NOT EXISTS IX_process_log_created_at ON process_log (created_at);
                CREATE INDEX IF NOT EXISTS IX_process_log_student_id ON process_log (student_id);",

            _ => null
        };
    }
}
