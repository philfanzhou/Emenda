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
            "mistake_item" => @"
                CREATE TABLE IF NOT EXISTS mistake_item (
                    id uuid NOT NULL,
                    student_id character varying(36) NOT NULL,
                    subject integer NOT NULL,
                    grade integer NOT NULL,
                    source_upload_id character varying(64) NOT NULL,
                    source_image_path text NOT NULL,
                    bbox_x1 integer NULL,
                    bbox_y1 integer NULL,
                    bbox_x2 integer NULL,
                    bbox_y2 integer NULL,
                    question_number character varying(20) NULL,
                    question_text text NULL,
                    student_answer text NULL,
                    ai_confidence double precision NOT NULL DEFAULT 0,
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
