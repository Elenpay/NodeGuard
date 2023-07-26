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

namespace NodeGuard.Migrations
{
    public partial class KeyFKInternalWallet : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFundsManagerPrivateKey",
                table: "Keys");

            migrationBuilder.AddColumn<int>(
                name: "InternalWalletId",
                table: "Keys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MasterFingerprint",
                table: "InternalWallets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Keys_InternalWalletId",
                table: "Keys",
                column: "InternalWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_InternalWallets_InternalWalletId",
                table: "Keys",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Keys_InternalWallets_InternalWalletId",
                table: "Keys");

            migrationBuilder.DropIndex(
                name: "IX_Keys_InternalWalletId",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "InternalWalletId",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "MasterFingerprint",
                table: "InternalWallets");

            migrationBuilder.AddColumn<bool>(
                name: "IsFundsManagerPrivateKey",
                table: "Keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
