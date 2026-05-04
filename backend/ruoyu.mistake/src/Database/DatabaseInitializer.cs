using Ruoyu.Study.Common.Database;

namespace Ruoyu.Study.Mistake.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(MistakeDbContext context, Microsoft.Extensions.Logging.ILogger logger)
    {
        await Common.Database.DatabaseInitializer.InitializeAsync(context, logger, GetTableCreationSql);
    }

    private static string? GetTableCreationSql(string tableName)
    {
        return tableName switch
        {
            "mistake" => @"
                CREATE TABLE IF NOT EXISTS mistake (
                    id uuid NOT NULL,
                    student_id character varying(36) NOT NULL,
                    subject integer NOT NULL,
                    grade integer NOT NULL,
                    date timestamp with time zone NOT NULL,
                    type integer NOT NULL,
                    question_id character varying(36) NULL,
                    status integer NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NULL,
                    CONSTRAINT PK_mistake PRIMARY KEY (id)
                );
                CREATE INDEX IF NOT EXISTS IX_mistake_student_id ON mistake (student_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_student_id_subject_grade_status ON mistake (student_id, subject, grade, status);
                CREATE INDEX IF NOT EXISTS IX_mistake_type ON mistake (type);
                CREATE INDEX IF NOT EXISTS IX_mistake_status ON mistake (status);
                CREATE INDEX IF NOT EXISTS IX_mistake_date ON mistake (date);
                CREATE INDEX IF NOT EXISTS IX_mistake_created_at ON mistake (created_at);",

            "mistake_detail" => @"
                CREATE TABLE IF NOT EXISTS mistake_detail (
                    id uuid NOT NULL,
                    mistake_id uuid NOT NULL,
                    original_images text NOT NULL DEFAULT '[]',
                    comments text NULL,
                    CONSTRAINT PK_mistake_detail PRIMARY KEY (id),
                    CONSTRAINT FK_mistake_detail_mistake_mistake_id 
                        FOREIGN KEY (mistake_id) REFERENCES mistake(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_mistake_detail_mistake_id ON mistake_detail (mistake_id);",

            "mistake_practice" => @"
                CREATE TABLE IF NOT EXISTS mistake_practice (
                    id uuid NOT NULL,
                    mistake_id uuid NOT NULL,
                    student_id character varying(36) NOT NULL,
                    practice_date timestamp with time zone NOT NULL,
                    image_paths text NOT NULL DEFAULT '[]',
                    student_answer text NULL,
                    is_correct boolean NOT NULL,
                    comments text NULL,
                    CONSTRAINT PK_mistake_practice PRIMARY KEY (id),
                    CONSTRAINT FK_mistake_practice_mistake_mistake_id 
                        FOREIGN KEY (mistake_id) REFERENCES mistake(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_mistake_practice_mistake_id ON mistake_practice (mistake_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_practice_student_id ON mistake_practice (student_id);
                CREATE INDEX IF NOT EXISTS IX_mistake_practice_practice_date ON mistake_practice (practice_date);",

            "process_log" => @"
                CREATE TABLE IF NOT EXISTS process_log (
                    id uuid NOT NULL,
                    mistake_id uuid NOT NULL,
                    action integer NOT NULL,
                    details text NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_process_log PRIMARY KEY (id),
                    CONSTRAINT FK_process_log_mistake_mistake_id 
                        FOREIGN KEY (mistake_id) REFERENCES mistake(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_process_log_mistake_id_created_at ON process_log (mistake_id, created_at);
                CREATE INDEX IF NOT EXISTS IX_process_log_created_at ON process_log (created_at);",

            _ => null
        };
    }
}
