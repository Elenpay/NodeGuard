using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class RemoveCycleInternalWallet : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_InternalWallets_InternalWalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequests_InternalWalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "InternalWalletId",
                table: "ChannelOperationRequests");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InternalWalletId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_InternalWalletId",
                table: "ChannelOperationRequests",
                column: "InternalWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_InternalWallets_InternalWalletId",
                table: "ChannelOperationRequests",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
