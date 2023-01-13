/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class NodeReturningFundsMultisigWallet : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReturningFundsMultisigWalletId",
                table: "Nodes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ReturningFundsMultisigWalletId",
                table: "Nodes",
                column: "ReturningFundsMultisigWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsMultisigWalletId",
                table: "Nodes",
                column: "ReturningFundsMultisigWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsMultisigWalletId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_ReturningFundsMultisigWalletId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ReturningFundsMultisigWalletId",
                table: "Nodes");
        }
    }
}
