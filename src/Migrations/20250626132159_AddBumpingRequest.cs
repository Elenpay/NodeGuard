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
                name: "BumpingId",
                table: "WalletWithdrawalRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletWithdrawalRequests_BumpingId",
                table: "WalletWithdrawalRequests",
                column: "BumpingId");

            migrationBuilder.AddForeignKey(
                name: "FK_WalletWithdrawalRequests_WalletWithdrawalRequests_BumpingId",
                table: "WalletWithdrawalRequests",
                column: "BumpingId",
                principalTable: "WalletWithdrawalRequests",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WalletWithdrawalRequests_WalletWithdrawalRequests_BumpingId",
                table: "WalletWithdrawalRequests");

            migrationBuilder.DropIndex(
                name: "IX_WalletWithdrawalRequests_BumpingId",
                table: "WalletWithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "BumpingId",
                table: "WalletWithdrawalRequests");
        }
    }
}
