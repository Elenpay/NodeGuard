using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class OpRequestAmountInSatoshis : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Amount",
                table: "ChannelOperationRequests");

            migrationBuilder.AddColumn<long>(
                name: "SatsAmount",
                table: "ChannelOperationRequests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SatsAmount",
                table: "ChannelOperationRequests");

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "ChannelOperationRequests",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }
    }
}
