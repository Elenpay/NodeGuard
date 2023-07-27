using System.Collections.Generic;
using NodeGuard.Helpers;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    public partial class NullableStatusLogs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<List<ChannelStatusLog>>(
                name: "StatusLogs",
                table: "ChannelOperationRequests",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(List<ChannelStatusLog>),
                oldType: "jsonb");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<List<ChannelStatusLog>>(
                name: "StatusLogs",
                table: "ChannelOperationRequests",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(List<ChannelStatusLog>),
                oldType: "jsonb",
                oldNullable: true);
        }
    }
}
