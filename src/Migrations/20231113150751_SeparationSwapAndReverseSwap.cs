// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

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
            migrationBuilder.AddColumn<int>(
                name: "SwapWalletId",
                table: "LiquidityRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(@"UPDATE ""LiquidityRules"" SET ""SwapWalletId"" = ""WalletId""");

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
