namespace Emenda.Service.Tests;

/// <summary>
/// DDL replayed from the automatic schema maintenance of historical builds, used to recreate the database shapes
/// those builds left behind. Sources: backend/ruoyu.mistake 20260501042151_InitialCreate and
/// 20260503021905_RemoveImageFingerprint (f37c8b1d, b70f0461); mistake_item layouts of da97cc6d, 05a5bf16 and
/// e0e56e2b; DatabaseInitializer of 07165ca8 through 25affa31^ (old-table conversion, column renames, history stamp,
/// process_log.student_id and review_comment renames); EF script of 20260626000000_AddReanalyzeJob.
/// </summary>
internal static class LegacyShapeFixtures
{
    internal const string Layout0501 = """
        CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL, "ProductVersion" character varying(32) NOT NULL, CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));
        CREATE TABLE mistake (id uuid NOT NULL, student_id character varying(36) NOT NULL, subject integer NOT NULL, grade integer NOT NULL, date timestamp with time zone NOT NULL, type integer NOT NULL, question_id character varying(36), status integer NOT NULL, created_at timestamp with time zone NOT NULL, updated_at timestamp with time zone, CONSTRAINT "PK_mistake" PRIMARY KEY (id));
        CREATE TABLE mistake_detail (id uuid NOT NULL, mistake_id uuid NOT NULL, original_images text NOT NULL, comments text, CONSTRAINT "PK_mistake_detail" PRIMARY KEY (id), CONSTRAINT "FK_mistake_detail_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE TABLE mistake_practice (id uuid NOT NULL, mistake_id uuid NOT NULL, student_id character varying(36) NOT NULL, practice_date timestamp with time zone NOT NULL, image_paths text NOT NULL, student_answer text, is_correct boolean NOT NULL, comments text, CONSTRAINT "PK_mistake_practice" PRIMARY KEY (id), CONSTRAINT "FK_mistake_practice_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE TABLE process_log (id uuid NOT NULL, mistake_id uuid NOT NULL, action integer NOT NULL, details text, created_at timestamp with time zone NOT NULL, CONSTRAINT "PK_process_log" PRIMARY KEY (id), CONSTRAINT "FK_process_log_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE INDEX "IX_mistake_created_at" ON mistake (created_at);
        CREATE INDEX "IX_mistake_date" ON mistake (date);
        CREATE INDEX "IX_mistake_status" ON mistake (status);
        CREATE INDEX "IX_mistake_student_id" ON mistake (student_id);
        CREATE INDEX "IX_mistake_student_id_subject_grade_status" ON mistake (student_id, subject, grade, status);
        CREATE INDEX "IX_mistake_type" ON mistake (type);
        CREATE UNIQUE INDEX "IX_mistake_detail_mistake_id" ON mistake_detail (mistake_id);
        CREATE INDEX "IX_mistake_practice_mistake_id" ON mistake_practice (mistake_id);
        CREATE INDEX "IX_mistake_practice_practice_date" ON mistake_practice (practice_date);
        CREATE INDEX "IX_mistake_practice_student_id" ON mistake_practice (student_id);
        CREATE INDEX "IX_process_log_created_at" ON process_log (created_at);
        CREATE INDEX "IX_process_log_mistake_id_created_at" ON process_log (mistake_id, created_at);
        INSERT INTO "__EFMigrationsHistory" VALUES ('20260501042151_InitialCreate','8.0.4');
        INSERT INTO mistake VALUES ('11111111-1111-1111-1111-111111111111','s1',1,7,now(),0,NULL,1,now(),NULL),('22222222-2222-2222-2222-222222222222','s1',1,7,now(),0,NULL,2,now(),NULL);
        INSERT INTO mistake_detail VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111','["mistakes/s1/a.jpg","mistakes/s1/b.jpg"]','老备注');
        INSERT INTO mistake_practice VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111','s1',now(),'["mistakes/s1/p.jpg"]',NULL,true,NULL);
        INSERT INTO process_log VALUES (gen_random_uuid(),'22222222-2222-2222-2222-222222222222',1,'created',now());
        CREATE TABLE mistake_image_fingerprint (id uuid NOT NULL, hash character varying(64) NOT NULL, student_id character varying(36) NOT NULL, mistake_id uuid NOT NULL, image_index integer NOT NULL, oss_path text NOT NULL, created_at timestamp with time zone NOT NULL, CONSTRAINT "PK_mistake_image_fingerprint" PRIMARY KEY (id), CONSTRAINT "FK_mistake_image_fingerprint_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE UNIQUE INDEX "IX_mistake_image_fingerprint_hash_student_id" ON mistake_image_fingerprint (hash, student_id);
        CREATE INDEX "IX_mistake_image_fingerprint_mistake_id" ON mistake_image_fingerprint (mistake_id);
        CREATE INDEX "IX_mistake_image_fingerprint_oss_path" ON mistake_image_fingerprint (oss_path);
        INSERT INTO mistake_image_fingerprint VALUES (gen_random_uuid(),'abc','s1','11111111-1111-1111-1111-111111111111',0,'mistakes/s1/a.jpg',now());
        """;

    internal const string Layout0503 = """
        CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL, "ProductVersion" character varying(32) NOT NULL, CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));
        CREATE TABLE mistake (id uuid NOT NULL, student_id character varying(36) NOT NULL, subject integer NOT NULL, grade integer NOT NULL, date timestamp with time zone NOT NULL, type integer NOT NULL, question_id character varying(36), status integer NOT NULL, created_at timestamp with time zone NOT NULL, updated_at timestamp with time zone, CONSTRAINT "PK_mistake" PRIMARY KEY (id));
        CREATE TABLE mistake_detail (id uuid NOT NULL, mistake_id uuid NOT NULL, original_images text NOT NULL, comments text, CONSTRAINT "PK_mistake_detail" PRIMARY KEY (id), CONSTRAINT "FK_mistake_detail_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE TABLE mistake_practice (id uuid NOT NULL, mistake_id uuid NOT NULL, student_id character varying(36) NOT NULL, practice_date timestamp with time zone NOT NULL, image_paths text NOT NULL, student_answer text, is_correct boolean NOT NULL, comments text, CONSTRAINT "PK_mistake_practice" PRIMARY KEY (id), CONSTRAINT "FK_mistake_practice_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE TABLE process_log (id uuid NOT NULL, mistake_id uuid NOT NULL, action integer NOT NULL, details text, created_at timestamp with time zone NOT NULL, CONSTRAINT "PK_process_log" PRIMARY KEY (id), CONSTRAINT "FK_process_log_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE INDEX "IX_mistake_created_at" ON mistake (created_at);
        CREATE INDEX "IX_mistake_date" ON mistake (date);
        CREATE INDEX "IX_mistake_status" ON mistake (status);
        CREATE INDEX "IX_mistake_student_id" ON mistake (student_id);
        CREATE INDEX "IX_mistake_student_id_subject_grade_status" ON mistake (student_id, subject, grade, status);
        CREATE INDEX "IX_mistake_type" ON mistake (type);
        CREATE UNIQUE INDEX "IX_mistake_detail_mistake_id" ON mistake_detail (mistake_id);
        CREATE INDEX "IX_mistake_practice_mistake_id" ON mistake_practice (mistake_id);
        CREATE INDEX "IX_mistake_practice_practice_date" ON mistake_practice (practice_date);
        CREATE INDEX "IX_mistake_practice_student_id" ON mistake_practice (student_id);
        CREATE INDEX "IX_process_log_created_at" ON process_log (created_at);
        CREATE INDEX "IX_process_log_mistake_id_created_at" ON process_log (mistake_id, created_at);
        INSERT INTO "__EFMigrationsHistory" VALUES ('20260501042151_InitialCreate','8.0.4'),('20260503021905_RemoveImageFingerprint','8.0.4');
        INSERT INTO mistake VALUES ('11111111-1111-1111-1111-111111111111','s1',1,7,now(),0,NULL,1,now(),NULL),('22222222-2222-2222-2222-222222222222','s1',1,7,now(),0,NULL,2,now(),NULL);
        INSERT INTO mistake_detail VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111','["mistakes/s1/a.jpg","mistakes/s1/b.jpg"]','老备注');
        INSERT INTO mistake_practice VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111','s1',now(),'["mistakes/s1/p.jpg"]',NULL,true,NULL);
        INSERT INTO process_log VALUES (gen_random_uuid(),'22222222-2222-2222-2222-222222222222',1,'created',now());
        """;

    internal const string Layout0503WithoutChildRows = """
        CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL, "ProductVersion" character varying(32) NOT NULL, CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));
        CREATE TABLE mistake (id uuid NOT NULL, student_id character varying(36) NOT NULL, subject integer NOT NULL, grade integer NOT NULL, date timestamp with time zone NOT NULL, type integer NOT NULL, question_id character varying(36), status integer NOT NULL, created_at timestamp with time zone NOT NULL, updated_at timestamp with time zone, CONSTRAINT "PK_mistake" PRIMARY KEY (id));
        CREATE TABLE mistake_detail (id uuid NOT NULL, mistake_id uuid NOT NULL, original_images text NOT NULL, comments text, CONSTRAINT "PK_mistake_detail" PRIMARY KEY (id), CONSTRAINT "FK_mistake_detail_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE TABLE mistake_practice (id uuid NOT NULL, mistake_id uuid NOT NULL, student_id character varying(36) NOT NULL, practice_date timestamp with time zone NOT NULL, image_paths text NOT NULL, student_answer text, is_correct boolean NOT NULL, comments text, CONSTRAINT "PK_mistake_practice" PRIMARY KEY (id), CONSTRAINT "FK_mistake_practice_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE TABLE process_log (id uuid NOT NULL, mistake_id uuid NOT NULL, action integer NOT NULL, details text, created_at timestamp with time zone NOT NULL, CONSTRAINT "PK_process_log" PRIMARY KEY (id), CONSTRAINT "FK_process_log_mistake_mistake_id" FOREIGN KEY (mistake_id) REFERENCES mistake (id) ON DELETE CASCADE);
        CREATE INDEX "IX_mistake_created_at" ON mistake (created_at);
        CREATE INDEX "IX_mistake_date" ON mistake (date);
        CREATE INDEX "IX_mistake_status" ON mistake (status);
        CREATE INDEX "IX_mistake_student_id" ON mistake (student_id);
        CREATE INDEX "IX_mistake_student_id_subject_grade_status" ON mistake (student_id, subject, grade, status);
        CREATE INDEX "IX_mistake_type" ON mistake (type);
        CREATE UNIQUE INDEX "IX_mistake_detail_mistake_id" ON mistake_detail (mistake_id);
        CREATE INDEX "IX_mistake_practice_mistake_id" ON mistake_practice (mistake_id);
        CREATE INDEX "IX_mistake_practice_practice_date" ON mistake_practice (practice_date);
        CREATE INDEX "IX_mistake_practice_student_id" ON mistake_practice (student_id);
        CREATE INDEX "IX_process_log_created_at" ON process_log (created_at);
        CREATE INDEX "IX_process_log_mistake_id_created_at" ON process_log (mistake_id, created_at);
        INSERT INTO "__EFMigrationsHistory" VALUES ('20260501042151_InitialCreate','8.0.4'),('20260503021905_RemoveImageFingerprint','8.0.4');
        INSERT INTO mistake VALUES ('11111111-1111-1111-1111-111111111111','s1',1,7,now(),0,NULL,1,now(),NULL),('22222222-2222-2222-2222-222222222222','s1',1,7,now(),0,NULL,2,now(),NULL);
        INSERT INTO mistake_detail VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111','["mistakes/s1/a.jpg","mistakes/s1/b.jpg"]','老备注');
        """;

    internal const string ConvertHead = """
        DO $$ DECLARE r RECORD; BEGIN FOR r IN SELECT conname, conrelid::regclass AS tbl FROM pg_constraint WHERE contype='f' AND confrelid='public.mistake'::regclass LOOP EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', r.tbl, r.conname); END LOOP; END $$;
        ALTER TABLE mistake RENAME TO mistake_item;
        """;

    internal const string ConvertWithoutType = """
        ALTER TABLE mistake_item ADD COLUMN IF NOT EXISTS source_upload_id character varying(64) NOT NULL DEFAULT '', ADD COLUMN IF NOT EXISTS source_regions jsonb NOT NULL DEFAULT '[]', ADD COLUMN IF NOT EXISTS reviewer_id character varying(36) NULL, ADD COLUMN IF NOT EXISTS reviewed_at timestamp with time zone NULL, ADD COLUMN IF NOT EXISTS root_cause text NULL, ADD COLUMN IF NOT EXISTS question_id character varying(36) NULL;
        DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='mistake_item' AND column_name='status') THEN ALTER TABLE mistake_item RENAME COLUMN status TO review_status; END IF; END $$;
        ALTER TABLE mistake_item DROP COLUMN IF EXISTS date, DROP COLUMN IF EXISTS image_paths, DROP COLUMN IF EXISTS fingerprint, DROP COLUMN IF EXISTS type;
        """;

    internal const string ConvertKeepType = """
        ALTER TABLE mistake_item ADD COLUMN IF NOT EXISTS source_upload_id character varying(64) NOT NULL DEFAULT '', ADD COLUMN IF NOT EXISTS source_regions jsonb NOT NULL DEFAULT '[]', ADD COLUMN IF NOT EXISTS reviewer_id character varying(36) NULL, ADD COLUMN IF NOT EXISTS reviewed_at timestamp with time zone NULL, ADD COLUMN IF NOT EXISTS review_comment text NULL, ADD COLUMN IF NOT EXISTS type integer NOT NULL DEFAULT 0, ADD COLUMN IF NOT EXISTS question_id character varying(36) NULL;
        DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='mistake_item' AND column_name='status') THEN ALTER TABLE mistake_item RENAME COLUMN status TO review_status; END IF; END $$;
        ALTER TABLE mistake_item DROP COLUMN IF EXISTS date, DROP COLUMN IF EXISTS image_paths, DROP COLUMN IF EXISTS fingerprint;
        """;

    internal const string ConvertTail = """
        DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='mistake_item' AND column_name='status') THEN ALTER TABLE mistake_item RENAME COLUMN status TO review_status; END IF; END $$;
        CREATE INDEX IF NOT EXISTS IX_mistake_item_student_id ON mistake_item (student_id);
        CREATE INDEX IF NOT EXISTS IX_mistake_item_student_id_subject_grade_review_status ON mistake_item (student_id, subject, grade, review_status);
        CREATE INDEX IF NOT EXISTS IX_mistake_item_source_upload_id ON mistake_item (source_upload_id);
        CREATE INDEX IF NOT EXISTS IX_mistake_item_review_status ON mistake_item (review_status);
        CREATE INDEX IF NOT EXISTS IX_mistake_item_created_at ON mistake_item (created_at);
        """;

    internal const string StampInitial = """
        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL, "ProductVersion" character varying(32) NOT NULL, CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));
        INSERT INTO "__EFMigrationsHistory" VALUES ('20260514051544_InitialCreate','8.0.11') ON CONFLICT DO NOTHING;
        """;

    internal const string Era0612 = """
        DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='process_log' AND column_name='student_id') THEN ALTER TABLE process_log ADD COLUMN student_id character varying(36) NULL; END IF; END $$;
        CREATE INDEX IF NOT EXISTS IX_process_log_student_id ON process_log (student_id);
        INSERT INTO "__EFMigrationsHistory" VALUES ('20260612030015_AddStudentIdToProcessLog','8.0.11') ON CONFLICT DO NOTHING;
        """;

    internal const string Era0620 = """
        DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='mistake_item' AND column_name='review_comment') THEN ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause; END IF; END $$;
        """;

    internal const string AddType = """
        ALTER TABLE mistake_item ADD COLUMN type integer NOT NULL;
        """;

    internal const string DropMixed = """
        DO $$ DECLARE r RECORD; BEGIN FOR r IN SELECT conname, conrelid::regclass AS tbl FROM pg_constraint WHERE contype='f' AND confrelid='public.mistake'::regclass LOOP EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', r.tbl, r.conname); END LOOP; END $$;
        DROP TABLE IF EXISTS mistake CASCADE;
        """;

    internal const string Reanalyze = """
        CREATE TABLE reanalyze_job (
        id uuid NOT NULL,
        item_id uuid NOT NULL,
        reviewer_id character varying(36) NOT NULL,
        student_id character varying(36) NOT NULL,
        teacher_description text,
        previous_root_cause text,
        status integer NOT NULL DEFAULT 1,
        error_message text,
        new_root_cause text,
        created_at timestamp with time zone NOT NULL,
        started_at timestamp with time zone,
        completed_at timestamp with time zone,
        vl_duration_ms bigint,
        CONSTRAINT "PK_reanalyze_job" PRIMARY KEY (id)
        );
        CREATE INDEX "IX_reanalyze_job_Status_CreatedAt" ON reanalyze_job (status, created_at);
        CREATE INDEX "IX_reanalyze_job_ReviewerId_Status" ON reanalyze_job (reviewer_id, status);
        CREATE INDEX "IX_reanalyze_job_CompletedAt" ON reanalyze_job (completed_at);
        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ('20260626000000_AddReanalyzeJob', '8.0.11');
        """;

    internal const string ItemLayoutE0e56e2b = """
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
        CREATE INDEX IF NOT EXISTS IX_mistake_item_created_at ON mistake_item (created_at);
        """;

    internal const string ItemLayoutDa97cc6d = """
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
        CREATE INDEX IF NOT EXISTS IX_mistake_item_created_at ON mistake_item (created_at);
        """;

    internal const string ItemLayout05a5bf16 = """
        CREATE TABLE IF NOT EXISTS mistake_item (
        id uuid NOT NULL,
        student_id character varying(36) NOT NULL,
        subject integer NOT NULL,
        grade integer NOT NULL,
        source_upload_id character varying(64) NOT NULL,
        source_regions jsonb NOT NULL DEFAULT '[]',
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
        CREATE INDEX IF NOT EXISTS IX_mistake_item_created_at ON mistake_item (created_at);
        """;

    internal const string ItemRowE0e56e2b = """
        INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,review_status,created_at) VALUES ('33333333-3333-3333-3333-333333333333','s1',1,7,'u1','[{"sourceImagePath":"mistakes/s1/c.jpg","boundingBox":null}]',1,now());
        """;

    internal const string ItemRowDa97cc6d = """
        INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_image_path,bbox_x1,bbox_y1,bbox_x2,bbox_y2,question_number,review_status,created_at) VALUES ('33333333-3333-3333-3333-333333333333','s1',1,7,'u1','mistakes/s1/c.jpg',1,2,30,40,'3',1,now());
        """;

    internal const string ItemRow05a5bf16 = """
        INSERT INTO mistake_item (id,student_id,subject,grade,source_upload_id,source_regions,question_number,review_status,created_at) VALUES ('33333333-3333-3333-3333-333333333333','s1',1,7,'u1','[{"sourceImagePath":"mistakes/s1/c.jpg","boundingBox":null}]','3',1,now());
        """;

    /// <summary>07165ca8+ column renames; old builds ran each statement separately and stopped at the first failure.</summary>
    internal static readonly string[] Renames0514 =
    [
        "DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='process_log' AND column_name='mistake_id') THEN ALTER TABLE process_log RENAME COLUMN mistake_id TO mistake_item_id; END IF; END $$;",
        "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE LOWER(conname)='fk_process_log_mistake_item_mistake_item_id') THEN ALTER TABLE process_log ADD CONSTRAINT FK_process_log_mistake_item_mistake_item_id FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE; END IF; END $$;",
        "DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='mistake_practice' AND column_name='mistake_id') THEN ALTER TABLE mistake_practice RENAME COLUMN mistake_id TO mistake_item_id; END IF; END $$;",
        "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE LOWER(conname)='fk_mistake_practice_mistake_item_mistake_item_id') THEN ALTER TABLE mistake_practice ADD CONSTRAINT FK_mistake_practice_mistake_item_mistake_item_id FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE; END IF; END $$;",
    ];

    /// <summary>06-12+ order of the same renames, including process_log.student_id.</summary>
    internal static readonly string[] RenamesLate =
    [
        "DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='mistake_item' AND column_name='review_comment') THEN ALTER TABLE mistake_item RENAME COLUMN review_comment TO root_cause; END IF; END $$;",
        "DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='process_log' AND column_name='mistake_id') THEN ALTER TABLE process_log RENAME COLUMN mistake_id TO mistake_item_id; END IF; END $$;",
        "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='process_log' AND column_name='student_id') THEN ALTER TABLE process_log ADD COLUMN student_id character varying(36) NULL; END IF; END $$;",
        "CREATE INDEX IF NOT EXISTS IX_process_log_student_id ON process_log (student_id);",
        "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE LOWER(conname)='fk_process_log_mistake_item_mistake_item_id') THEN ALTER TABLE process_log ADD CONSTRAINT FK_process_log_mistake_item_mistake_item_id FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE; END IF; END $$;",
    ];
}
