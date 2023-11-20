using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class SeparationSwapAndReverseSwap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRules_Wallets_WalletId",
                table: "LiquidityRules");

            migrationBuilder.RenameColumn(
                name: "WalletId",
                table: "LiquidityRules",
                newName: "ReverseSwapWalletId");

            migrationBuilder.RenameColumn(
                name: "IsWalletRule",
                table: "LiquidityRules",
                newName: "IsReverseSwapWalletRule");

            migrationBuilder.RenameColumn(
                name: "Address",
                table: "LiquidityRules",
                newName: "ReverseSwapAddress");

            migrationBuilder.RenameIndex(
                name: "IX_LiquidityRules_WalletId",
                table: "LiquidityRules",
                newName: "IX_LiquidityRules_ReverseSwapWalletId");

            migrationBuilder.AddColumn<int>(
                name: "SwapWalletId",
                table: "LiquidityRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_LiquidityRules_SwapWalletId",
                table: "LiquidityRules",
                column: "SwapWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRules_Wallets_ReverseSwapWalletId",
                table: "LiquidityRules",
                column: "ReverseSwapWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRules_Wallets_SwapWalletId",
                table: "LiquidityRules",
                column: "SwapWalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRules_Wallets_ReverseSwapWalletId",
                table: "LiquidityRules");

            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRules_Wallets_SwapWalletId",
                table: "LiquidityRules");

            migrationBuilder.DropIndex(
                name: "IX_LiquidityRules_SwapWalletId",
                table: "LiquidityRules");

            migrationBuilder.DropColumn(
                name: "SwapWalletId",
                table: "LiquidityRules");

            migrationBuilder.RenameColumn(
                name: "ReverseSwapWalletId",
                table: "LiquidityRules",
                newName: "WalletId");

            migrationBuilder.RenameColumn(
                name: "ReverseSwapAddress",
                table: "LiquidityRules",
                newName: "Address");

            migrationBuilder.RenameColumn(
                name: "IsReverseSwapWalletRule",
                table: "LiquidityRules",
                newName: "IsWalletRule");

            migrationBuilder.RenameIndex(
                name: "IX_LiquidityRules_ReverseSwapWalletId",
                table: "LiquidityRules",
                newName: "IX_LiquidityRules_WalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRules_Wallets_WalletId",
                table: "LiquidityRules",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }
    }
}
