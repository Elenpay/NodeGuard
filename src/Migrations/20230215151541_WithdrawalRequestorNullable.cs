using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class WithdrawalRequestorNullable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WalletWithdrawalRequests_AspNetUsers_UserRequestorId",
                table: "WalletWithdrawalRequests");

            migrationBuilder.AlterColumn<string>(
                name: "UserRequestorId",
                table: "WalletWithdrawalRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddForeignKey(
                name: "FK_WalletWithdrawalRequests_AspNetUsers_UserRequestorId",
                table: "WalletWithdrawalRequests",
                column: "UserRequestorId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WalletWithdrawalRequests_AspNetUsers_UserRequestorId",
                table: "WalletWithdrawalRequests");

            migrationBuilder.AlterColumn<string>(
                name: "UserRequestorId",
                table: "WalletWithdrawalRequests",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WalletWithdrawalRequests_AspNetUsers_UserRequestorId",
                table: "WalletWithdrawalRequests",
                column: "UserRequestorId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
