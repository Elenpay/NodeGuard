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
    public partial class BIP39Import : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets");

            migrationBuilder.AlterColumn<int>(
                name: "InternalWalletId",
                table: "Wallets",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "IsBIP39Imported",
                table: "Wallets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsBIP39ImportedKey",
                table: "Keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "IsBIP39Imported",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "IsBIP39ImportedKey",
                table: "Keys");

            migrationBuilder.AlterColumn<int>(
                name: "InternalWalletId",
                table: "Wallets",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
