using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class WalletInLiquidityRuleNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRules_Wallets_WalletId",
                table: "LiquidityRules");

            migrationBuilder.AlterColumn<int>(
                name: "WalletId",
                table: "LiquidityRules",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRules_Wallets_WalletId",
                table: "LiquidityRules",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRules_Wallets_WalletId",
                table: "LiquidityRules");

            migrationBuilder.AlterColumn<int>(
                name: "WalletId",
                table: "LiquidityRules",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRules_Wallets_WalletId",
                table: "LiquidityRules",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
