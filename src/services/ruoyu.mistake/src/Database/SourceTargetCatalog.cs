namespace Ruoyu.Study.Mistake.Database;

internal static class SourceTargetCatalog
{
    internal static readonly string[] Tables=["source_target_object","source_target_attempt"];
    internal static readonly string[] Columns=[
        "source_target_object|source_upload_id|uuid|N","source_target_object|content_revision|uuid|N","source_target_object|source_path|text|N",
        "source_target_object|snapshot_id|uuid|N","source_target_object|student_id|character varying(36)|N",
        "source_target_object|expected_length|bigint|N","source_target_object|expected_sha256|character varying(64)|N",
        "source_target_object|selected_attempt_id|uuid|Y",
        "source_target_attempt|attempt_id|uuid|N","source_target_attempt|source_upload_id|uuid|N","source_target_attempt|content_revision|uuid|N",
        "source_target_attempt|source_path|text|N","source_target_attempt|target_key|text|N","source_target_attempt|owner_id|uuid|N",
        "source_target_attempt|fence|bigint|N","source_target_attempt|phase|text|N","source_target_attempt|verified_length|bigint|Y",
        "source_target_attempt|verified_sha256|character varying(64)|Y"];
    internal static readonly string[] Indexes=[
        "PK_source_target_object|source_target_object|source_upload_id,content_revision,source_path|U",
        "IX_source_target_object_selected_attempt_id|source_target_object|selected_attempt_id|N",
        "PK_source_target_attempt|source_target_attempt|attempt_id|U",
        "IX_source_target_attempt_key|source_target_attempt|target_key|U",
        "IX_source_target_attempt_object|source_target_attempt|source_upload_id,content_revision,source_path|N"];
    internal static readonly string[] Constraints=[
        "PK_source_target_object|p|source_upload_id,content_revision,source_path|-|-|-|V",
        "PK_source_target_attempt|p|attempt_id|-|-|-|V",
        "FK_source_target_object_intake|f|source_upload_id,content_revision|source_intake|source_upload_id,content_revision|r|V",
        "FK_source_target_object_selected|f|selected_attempt_id|source_target_attempt|attempt_id|r|V",
        "FK_source_target_attempt_object|f|source_upload_id,content_revision,source_path|source_target_object|source_upload_id,content_revision,source_path|r|V"];
}
