using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Emenda.Database.Migrations;

[DbContext(typeof(MistakeDbContext))]
[Migration("20260930000001_LeaseReanalyzeJobs")]
public sealed class LeaseReanalyzeJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid?>("owner_id", "reanalyze_job", "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset?>("lease_until", "reanalyze_job", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<long>("fencing_version", "reanalyze_job", "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<int>("attempt", "reanalyze_job", "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("execution_root_cause", "reanalyze_job", "text", nullable: true);
        migrationBuilder.AddColumn<string>("execution_input_digest", "reanalyze_job", "character varying(64)", maxLength: 64, nullable: true);
        // The upgrade command requires every old worker to have stopped. Their ownerless Running rows
        // cannot acquire a lease; fail them before any new worker can claim Pending jobs.
        migrationBuilder.Sql("""
            WITH interrupted AS (
                UPDATE reanalyze_job SET status=4, error_message='upgrade_interrupted', completed_at=clock_timestamp()
                WHERE status=2 RETURNING id,item_id
            )
            INSERT INTO process_log (id,mistake_item_id,student_id,action,details,created_at)
            SELECT gen_random_uuid(), j.item_id, i.student_id, 7,
                   json_build_object('job_id',j.id,'new_root_cause','FAILED: upgrade_interrupted')::text,
                   clock_timestamp()
            FROM interrupted j JOIN mistake_item i ON i.id=j.item_id;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("execution_input_digest", "reanalyze_job");
        migrationBuilder.DropColumn("execution_root_cause", "reanalyze_job");
        migrationBuilder.DropColumn("attempt", "reanalyze_job");
        migrationBuilder.DropColumn("fencing_version", "reanalyze_job");
        migrationBuilder.DropColumn("lease_until", "reanalyze_job");
        migrationBuilder.DropColumn("owner_id", "reanalyze_job");
    }
}
