using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class ChanId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChannelId",
                table: "Channels");

            migrationBuilder.AddColumn<decimal>(
                name: "ChanId",
                table: "Channels",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChanId",
                table: "Channels");

            migrationBuilder.AddColumn<string>(
                name: "ChannelId",
                table: "Channels",
                type: "text",
                nullable: true);
        }
    }
}
