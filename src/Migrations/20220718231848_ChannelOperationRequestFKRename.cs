using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class ChannelOperationRequestFKRename : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChannelOpenRequestId",
                table: "ChannelOperationRequestSignatures");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChannelOpenRequestId",
                table: "ChannelOperationRequestSignatures",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
