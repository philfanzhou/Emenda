namespace Ruoyu.Study.Mistake.Database;

internal static class SourceHandoffCatalog
{
    internal static readonly string[] Tables=["source_handoff"];
    internal static readonly string[] Columns=[
        "source_handoff|source_upload_id|uuid|N",
        "source_handoff|content_revision|uuid|N",
        "source_handoff|snapshot_id|uuid|N",
        "source_handoff|student_id|text|N",
        "source_handoff|operation_id|text|N",
        "source_handoff|actor|text|N",
        "source_handoff|sealed_at|timestamp with time zone|N",
        "source_handoff|groups_canonical|text|N",
        "source_handoff|groups_digest|text|N",
        "source_handoff|targets|text|N",
        "source_handoff|payload_canonical|text|N",
        "source_handoff|payload_digest|text|N",
        "source_handoff|owner_id|uuid|N",
        "source_handoff|fence|bigint|N",
        "source_handoff|phase|text|N",
        "source_handoff|receipt|text|Y",
        "source_handoff|completed_at|timestamp with time zone|Y",
        "source_handoff|error_kind|text|Y",
];
    internal static readonly string[] Indexes=["PK_source_handoff|source_handoff|source_upload_id,content_revision|U","IX_source_handoff_snapshot|source_handoff|snapshot_id|U","IX_source_handoff_operation|source_handoff|operation_id|U"];
    internal static readonly string[] Constraints=["PK_source_handoff|p|source_upload_id,content_revision|-|-|-|V","FK_source_handoff_intake|f|source_upload_id,content_revision|source_intake|source_upload_id,content_revision|r|V"];
    internal static readonly string[] Triggers=["source_handoff|trg_source_handoff|31|guard_source_handoff|False|False","source_handoff|trg_handoff_truncate|34|guard_source_handoff|False|False"];
}
