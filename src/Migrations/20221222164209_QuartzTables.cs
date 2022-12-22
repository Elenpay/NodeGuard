using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class QuartzTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(File.ReadAllText("Migrations/quartz_tables_postgres.sql"));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "qrtz_fired_triggers");
            migrationBuilder.DropTable(name: "qrtz_paused_trigger_grps");
            migrationBuilder.DropTable(name: "qrtz_scheduler_state");
            migrationBuilder.DropTable(name: "qrtz_locks");
            migrationBuilder.DropTable(name: "qrtz_simprop_triggers");
            migrationBuilder.DropTable(name: "qrtz_simple_triggers");
            migrationBuilder.DropTable(name: "qrtz_cron_triggers");
            migrationBuilder.DropTable(name: "qrtz_blob_triggers");
            migrationBuilder.DropTable(name: "qrtz_triggers");
            migrationBuilder.DropTable(name: "qrtz_job_details");
            migrationBuilder.DropTable(name: "qrtz_calendars");
        }
    }
}
