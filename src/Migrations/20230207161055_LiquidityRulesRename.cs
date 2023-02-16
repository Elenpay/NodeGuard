using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class LiquidityRulesRename : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRule_Channels_ChannelId",
                table: "LiquidityRule");

            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRule_Wallets_WalletId",
                table: "LiquidityRule");

            migrationBuilder.DropPrimaryKey(
                name: "PK_LiquidityRule",
                table: "LiquidityRule");

            migrationBuilder.RenameTable(
                name: "LiquidityRule",
                newName: "LiquidityRules");

            migrationBuilder.RenameIndex(
                name: "IX_LiquidityRule_WalletId",
                table: "LiquidityRules",
                newName: "IX_LiquidityRules_WalletId");

            migrationBuilder.RenameIndex(
                name: "IX_LiquidityRule_ChannelId",
                table: "LiquidityRules",
                newName: "IX_LiquidityRules_ChannelId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_LiquidityRules",
                table: "LiquidityRules",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRules_Channels_ChannelId",
                table: "LiquidityRules",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRules_Wallets_WalletId",
                table: "LiquidityRules",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRules_Channels_ChannelId",
                table: "LiquidityRules");

            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRules_Wallets_WalletId",
                table: "LiquidityRules");

            migrationBuilder.DropPrimaryKey(
                name: "PK_LiquidityRules",
                table: "LiquidityRules");

            migrationBuilder.RenameTable(
                name: "LiquidityRules",
                newName: "LiquidityRule");

            migrationBuilder.RenameIndex(
                name: "IX_LiquidityRules_WalletId",
                table: "LiquidityRule",
                newName: "IX_LiquidityRule_WalletId");

            migrationBuilder.RenameIndex(
                name: "IX_LiquidityRules_ChannelId",
                table: "LiquidityRule",
                newName: "IX_LiquidityRule_ChannelId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_LiquidityRule",
                table: "LiquidityRule",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRule_Channels_ChannelId",
                table: "LiquidityRule",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRule_Wallets_WalletId",
                table: "LiquidityRule",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
