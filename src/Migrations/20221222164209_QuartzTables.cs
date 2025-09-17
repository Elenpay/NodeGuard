// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.



using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
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
