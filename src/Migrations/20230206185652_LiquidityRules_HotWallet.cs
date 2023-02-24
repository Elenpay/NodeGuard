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

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class LiquidityRules_HotWallet : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InternalWalletSubDerivationPath",
                table: "Wallets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHotWallet",
                table: "Wallets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceId",
                table: "Wallets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAutomatedLiquidityEnabled",
                table: "Channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "LiquidityRule",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MinimumLocalBalance = table.Column<decimal>(type: "numeric", nullable: true),
                    MinimumRemoteBalance = table.Column<decimal>(type: "numeric", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ChannelId = table.Column<int>(type: "integer", nullable: false),
                    WalletId = table.Column<int>(type: "integer", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiquidityRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiquidityRule_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LiquidityRule_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiquidityRule_ChannelId",
                table: "LiquidityRule",
                column: "ChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiquidityRule_WalletId",
                table: "LiquidityRule",
                column: "WalletId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiquidityRule");

            migrationBuilder.DropColumn(
                name: "InternalWalletSubDerivationPath",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "IsHotWallet",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "ReferenceId",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "IsAutomatedLiquidityEnabled",
                table: "Channels");
        }
    }
}
