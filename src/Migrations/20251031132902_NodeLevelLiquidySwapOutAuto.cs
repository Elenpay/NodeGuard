using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class NodeLevelLiquidySwapOutAuto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsWalletId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_ReturningFundsWalletId",
                table: "Nodes");

            migrationBuilder.RenameColumn(
                name: "ReturningFundsWalletId",
                table: "Nodes",
                newName: "FundsDestinationWalletId");

            migrationBuilder.AddColumn<bool>(
                name: "AutoLiquidityManagementEnabled",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxSwapsInFlight",
                table: "Nodes",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxSwapFeeRatio",
                table: "Nodes",
                type: "numeric",
                nullable: false,
                defaultValue: 0.005m); // 0.5%

            migrationBuilder.AddColumn<long>(
                name: "MinimumBalanceThresholdSats",
                table: "Nodes",
                type: "bigint",
                nullable: false,
                defaultValue: 100000000L); // 1 BTC

            migrationBuilder.AddColumn<TimeSpan>(
                name: "SwapBudgetRefreshInterval",
                table: "Nodes",
                type: "interval",
                nullable: false,
                defaultValue: TimeSpan.FromDays(1));

            migrationBuilder.AddColumn<long>(
                name: "SwapBudgetSats",
                table: "Nodes",
                type: "bigint",
                nullable: false,
                defaultValue: 500000000L); // 0.5 BTC

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SwapBudgetStartDatetime",
                table: "Nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SwapMaxAmountSats",
                table: "Nodes",
                type: "bigint",
                nullable: false,
                defaultValue: 250000000L); // 0.25 BTC

            migrationBuilder.AddColumn<long>(
                name: "SwapMinAmountSats",
                table: "Nodes",
                type: "bigint",
                nullable: false,
                defaultValue: 10000000L); // 0.01 BTC

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_FundsDestinationWalletId",
                table: "Nodes",
                column: "FundsDestinationWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_FundsDestinationWalletId",
                table: "Nodes",
                column: "FundsDestinationWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_FundsDestinationWalletId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_FundsDestinationWalletId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoLiquidityManagementEnabled",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "FundsDestinationWalletId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MaxSwapFeeRatio",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MinimumBalanceThresholdSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SwapBudgetRefreshInterval",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SwapBudgetSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SwapBudgetStartDatetime",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SwapMaxAmountSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SwapMinAmountSats",
                table: "Nodes");

            migrationBuilder.RenameColumn(
                name: "MaxSwapsInFlight",
                table: "Nodes",
                newName: "ReturningFundsWalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ReturningFundsWalletId",
                table: "Nodes",
                column: "ReturningFundsWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsWalletId",
                table: "Nodes",
                column: "ReturningFundsWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }
    }
}
