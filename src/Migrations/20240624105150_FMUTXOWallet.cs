using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class FMUTXOWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WalletId",
                table: "FMUTXOs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_FMUTXOs_WalletId",
                table: "FMUTXOs",
                column: "WalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_FMUTXOs_Wallets_WalletId",
                table: "FMUTXOs",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FMUTXOs_Wallets_WalletId",
                table: "FMUTXOs");

            migrationBuilder.DropIndex(
                name: "IX_FMUTXOs_WalletId",
                table: "FMUTXOs");

            migrationBuilder.DropColumn(
                name: "WalletId",
                table: "FMUTXOs");
        }
    }
}
