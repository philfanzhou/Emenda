using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Database;

namespace Ruoyu.Study.Mistake.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(MistakeDbContext context, Microsoft.Extensions.Logging.ILogger logger)
    {
        await MigrateOldSchemaAsync(context, logger);
        await Common.Database.DatabaseInitializer.InitializeAsync(context, logger, GetTableCreationSql);
    }

    private static async Task MigrateOldSchemaAsync(MistakeDbContext context, Microsoft.Extensions.Logging.ILogger logger)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = context.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = 'public' 
                    AND table_name = 'mistake'
                )";
            var oldTableExists = (bool)(await cmd.ExecuteScalarAsync())!;

            if (!oldTableExists) return;

            logger.LogInformation("检测到旧表 mistake，正在迁移到新表结构...");

            await using var dropOldFksCmd = context.Database.GetDbConnection().CreateCommand();
            dropOldFksCmd.CommandText = @"
                DO $$
                DECLARE
                    fk_record RECORD;
                BEGIN
                    FOR fk_record IN
                        SELECT conname, conrelid::regclass AS tbl
                        FROM pg_constraint
                        WHERE contype = 'f'
                        AND confrelid = 'public.mistake'::regclass
                    LOOP
                        EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', fk_record.tbl, fk_record.conname);
                    END LOOP;
                END $$;";
            await dropOldFksCmd.ExecuteNonQueryAsync();

            await using var dropOldIndexesCmd = context.Database.GetDbConnection().CreateCommand();
            dropOldIndexesCmd.CommandText = @"
                DO $$
                DECLARE
                    idx_record RECORD;
                BEGIN
                    FOR idx_record IN
                        SELECT indexname
                        FROM pg_indexes
                        WHERE tablename = 'mistake'
                        AND indexname != 'mistake_pkey'
                    LOOP
                        EXECUTE format('DROP INDEX IF EXISTS %I', idx_record.indexname);
                    END LOOP;
                END $$;";
            await dropOldIndexesCmd.ExecuteNonQueryAsync();

            await using var renameCmd = context.Database.GetDbConnection().CreateCommand();
            renameCmd.CommandText = "ALTER TABLE mistake RENAME TO mistake_item";
            await renameCmd.ExecuteNonQueryAsync();

            await using var addMissingColsCmd = context.Database.GetDbConnection().CreateCommand();
            addMissingColsCmd.CommandText = @"
                ALTER TABLE mistake_item
                    ADD COLUMN IF NOT EXISTS source_upload_id character varying(64) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS source_regions jsonb NOT NULL DEFAULT '[]',
                    ADD COLUMN IF NOT EXISTS reviewer_id character varying(36) NULL,
                    ADD COLUMN IF NOT EXISTS reviewed_at timestamp with time zone NULL,
                    ADD COLUMN IF NOT EXISTS review_comment text NULL,
                    ADD COLUMN IF NOT EXISTS type integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS question_id character varying(36) NULL;";
            await addMissingColsCmd.ExecuteNonQueryAsync();

            await using var renameStatusColCmd = context.Database.GetDbConnection().CreateCommand();
            renameStatusColCmd.CommandText = @"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'mistake_item' AND column_name = 'status'
                    ) THEN
                        ALTER TABLE mistake_item RENAME COLUMN status TO review_status;
                    END IF;
                END $$;";
            await renameStatusColCmd.ExecuteNonQueryAsync();

            await using var dropOldColsCmd = context.Database.GetDbConnection().CreateCommand();
            dropOldColsCmd.CommandText = @"
                ALTER TABLE mistake_item
                    DROP COLUMN IF EXISTS date,
                    DROP COLUMN IF EXISTS image_paths,
                    DROP COLUMN IF EXISTS fingerprint;";
            await dropOldColsCmd.ExecuteNonQueryAsync();

            await using var createNewIndexesCmd = context.Database.GetDbConnection().CreateCommand();
            createNewIndexesCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS IX_mistake_item_student_id ON mistake_item (student_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_item_student_id_subject_grade_review_status ON mistake_item (student_id, subject, grade, review_status);
                CREATE INDEX IF NOT EXISTS IX_mistake_item_source_upload_id ON mistake_item (source_upload_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_item_review_status ON mistake_item (review_status);
                CREATE INDEX IF NOT EXISTS IX_mistake_item_created_at ON mistake_item (created_at);";
            await createNewIndexesCmd.ExecuteNonQueryAsync();

            await using var fixProcessLogFkCmd = context.Database.GetDbConnection().CreateCommand();
            fixProcessLogFkCmd.CommandText = @"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'process_log' AND column_name = 'mistake_id'
                    ) THEN
                        ALTER TABLE process_log RENAME COLUMN mistake_id TO mistake_item_id;
                    END IF;
                END $$;";
            await fixProcessLogFkCmd.ExecuteNonQueryAsync();

            await using var addProcessLogFkCmd = context.Database.GetDbConnection().CreateCommand();
            addProcessLogFkCmd.CommandText = @"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'FK_process_log_mistake_item_mistake_item_id'
                    ) THEN
                        ALTER TABLE process_log
                            ADD CONSTRAINT FK_process_log_mistake_item_mistake_item_id
                            FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE;
                    END IF;
                END $$;";
            await addProcessLogFkCmd.ExecuteNonQueryAsync();

            await using var fixPracticeFkCmd = context.Database.GetDbConnection().CreateCommand();
            fixPracticeFkCmd.CommandText = @"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'mistake_practice' AND column_name = 'mistake_id'
                    ) THEN
                        ALTER TABLE mistake_practice RENAME COLUMN mistake_id TO mistake_item_id;
                    END IF;
                END $$;";
            await fixPracticeFkCmd.ExecuteNonQueryAsync();

            await using var addPracticeFkCmd = context.Database.GetDbConnection().CreateCommand();
            addPracticeFkCmd.CommandText = @"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'FK_mistake_practice_mistake_item_mistake_item_id'
                    ) THEN
                        ALTER TABLE mistake_practice
                            ADD CONSTRAINT FK_mistake_practice_mistake_item_mistake_item_id
                            FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE;
                    END IF;
                END $$;";
            await addPracticeFkCmd.ExecuteNonQueryAsync();

            logger.LogInformation("旧表 mistake 迁移到 mistake_item 完成");
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
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
                    review_comment text NULL,
                    type integer NOT NULL DEFAULT 0,
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
                    action integer NOT NULL,
                    details text NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_process_log PRIMARY KEY (id),
                    CONSTRAINT FK_process_log_mistake_item_mistake_item_id 
                        FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_process_log_mistake_item_id_created_at ON process_log (mistake_item_id, created_at);
                CREATE INDEX IF NOT EXISTS IX_process_log_created_at ON process_log (created_at);",

            _ => null
        };
    }
}
