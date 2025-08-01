using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddBumpingRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BumpingWalletWithdrawalRequestId",
                table: "WalletWithdrawalRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletWithdrawalRequests_BumpingWalletWithdrawalRequestId",
                table: "WalletWithdrawalRequests",
                column: "BumpingWalletWithdrawalRequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_WalletWithdrawalRequests_WalletWithdrawalRequests_BumpingWa~",
                table: "WalletWithdrawalRequests",
                column: "BumpingWalletWithdrawalRequestId",
                principalTable: "WalletWithdrawalRequests",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WalletWithdrawalRequests_WalletWithdrawalRequests_BumpingWa~",
                table: "WalletWithdrawalRequests");

            migrationBuilder.DropIndex(
                name: "IX_WalletWithdrawalRequests_BumpingWalletWithdrawalRequestId",
                table: "WalletWithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "BumpingWalletWithdrawalRequestId",
                table: "WalletWithdrawalRequests");
        }
    }
}
