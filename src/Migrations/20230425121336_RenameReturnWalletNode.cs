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
    public partial class RenameReturnWalletNode : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsMultisigWalletId",
                table: "Nodes");

            migrationBuilder.RenameColumn(
                name: "ReturningFundsMultisigWalletId",
                table: "Nodes",
                newName: "ReturningFundsWalletId");

            migrationBuilder.RenameIndex(
                name: "IX_Nodes_ReturningFundsMultisigWalletId",
                table: "Nodes",
                newName: "IX_Nodes_ReturningFundsWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsWalletId",
                table: "Nodes",
                column: "ReturningFundsWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsWalletId",
                table: "Nodes");

            migrationBuilder.RenameColumn(
                name: "ReturningFundsWalletId",
                table: "Nodes",
                newName: "ReturningFundsMultisigWalletId");

            migrationBuilder.RenameIndex(
                name: "IX_Nodes_ReturningFundsWalletId",
                table: "Nodes",
                newName: "IX_Nodes_ReturningFundsMultisigWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsMultisigWalletId",
                table: "Nodes",
                column: "ReturningFundsMultisigWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }
    }
}
