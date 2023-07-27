using System.Collections.Generic;
using FundsManager.Helpers;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
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
