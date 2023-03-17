using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class ChannelOperationReqUserWalletNullable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_AspNetUsers_UserId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Wallets_WalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<int>(
                name: "WalletId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "ChannelOperationRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_AspNetUsers_UserId",
                table: "ChannelOperationRequests",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Wallets_WalletId",
                table: "ChannelOperationRequests",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_AspNetUsers_UserId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Wallets_WalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<int>(
                name: "WalletId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "ChannelOperationRequests",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_AspNetUsers_UserId",
                table: "ChannelOperationRequests",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Wallets_WalletId",
                table: "ChannelOperationRequests",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
