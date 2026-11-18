namespace Emenda.Database;

/// <summary>Additive snapshots, durable admission and permanent replay receipts.</summary>
public static class ConversationMistakeSchema
{
    public const string Migration = "20261118000000_AddConversationMistakes";
    public const string Up = """
        ALTER TABLE mistake_item ADD COLUMN conversation_source jsonb;
        ALTER TABLE mistake_item ADD COLUMN conversation_caller text;
        ALTER TABLE mistake_item ADD COLUMN conversation_active boolean NOT NULL DEFAULT false;
        CREATE TABLE conversation_collection_job (
          id uuid NOT NULL, caller_name text NOT NULL, actor_id text NOT NULL, student_id text NOT NULL, owner_id text NOT NULL, grade integer NOT NULL,
          status text NOT NULL, attempt integer NOT NULL, lease_id uuid, lease_until timestamptz,
          next_attempt_at timestamptz NOT NULL, staged_regions jsonb NOT NULL, error_code text, result jsonb,
          created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL,
          CONSTRAINT "PK_conversation_collection_job" PRIMARY KEY(id)
        );
        CREATE INDEX "IX_conversation_collection_status" ON conversation_collection_job(status,next_attempt_at);
        CREATE TABLE conversation_mistake_receipt (
          event_id text NOT NULL, source_key text NOT NULL, student_id text NOT NULL, item_id uuid NOT NULL,
          position integer NOT NULL, content_hash text NOT NULL, observation jsonb NOT NULL, status text NOT NULL, created_at timestamptz NOT NULL,
          CONSTRAINT "PK_conversation_mistake_receipt" PRIMARY KEY(event_id)
        );
        CREATE INDEX "IX_conversation_mistake_source" ON conversation_mistake_receipt(source_key);
        """;
    public const string Down = """
        DO $$ BEGIN
          IF EXISTS(SELECT 1 FROM conversation_collection_job) OR EXISTS(SELECT 1 FROM conversation_mistake_receipt)
             OR EXISTS(SELECT 1 FROM mistake_item WHERE conversation_source IS NOT NULL)
          THEN RAISE EXCEPTION 'conversation_mistake_data_present'; END IF;
        END $$;
        DROP TABLE conversation_mistake_receipt;
        DROP TABLE conversation_collection_job;
        ALTER TABLE mistake_item DROP COLUMN conversation_caller, DROP COLUMN conversation_source;
        ALTER TABLE mistake_item DROP COLUMN conversation_active;
        """;
    internal static readonly string[] Tables = ["conversation_collection_job", "conversation_mistake_receipt"];
    internal static readonly string[] Columns = [
        "mistake_item|conversation_source|jsonb|Y", "mistake_item|conversation_caller|text|Y",
        "conversation_collection_job|caller_name|text|N",
        "mistake_item|conversation_active|boolean|N",
        "conversation_collection_job|id|uuid|N", "conversation_collection_job|actor_id|text|N", "conversation_collection_job|student_id|text|N",
        "conversation_collection_job|owner_id|text|N", "conversation_collection_job|grade|integer|N", "conversation_collection_job|status|text|N",
        "conversation_collection_job|attempt|integer|N", "conversation_collection_job|lease_id|uuid|Y", "conversation_collection_job|lease_until|timestamp with time zone|Y",
        "conversation_collection_job|next_attempt_at|timestamp with time zone|N", "conversation_collection_job|staged_regions|jsonb|N", "conversation_collection_job|error_code|text|Y",
        "conversation_collection_job|created_at|timestamp with time zone|N", "conversation_collection_job|updated_at|timestamp with time zone|N",
        "conversation_collection_job|result|jsonb|Y",
        "conversation_mistake_receipt|event_id|text|N", "conversation_mistake_receipt|source_key|text|N", "conversation_mistake_receipt|student_id|text|N",
        "conversation_mistake_receipt|item_id|uuid|N", "conversation_mistake_receipt|position|integer|N", "conversation_mistake_receipt|content_hash|text|N",
        "conversation_mistake_receipt|observation|jsonb|N", "conversation_mistake_receipt|status|text|N", "conversation_mistake_receipt|created_at|timestamp with time zone|N"];
    internal static readonly string[] Indexes = ["PK_conversation_collection_job|conversation_collection_job|id|U",
        "IX_conversation_collection_status|conversation_collection_job|status,next_attempt_at|N", "PK_conversation_mistake_receipt|conversation_mistake_receipt|event_id|U",
        "IX_conversation_mistake_source|conversation_mistake_receipt|source_key|N"];
    internal static readonly string[] Constraints = ["PK_conversation_collection_job|p|id|-|-|-|V", "PK_conversation_mistake_receipt|p|event_id|-|-|-|V"];
}
