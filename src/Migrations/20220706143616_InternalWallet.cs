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

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    public partial class InternalWallet : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "Capacity",
                table: "Channels");

            migrationBuilder.AddColumn<int>(
                name: "InternalWalletId",
                table: "Wallets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Keys",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "IsFundsManagerPrivateKey",
                table: "Keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "SatsAmount",
                table: "Channels",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<string>(
                name: "SignatureContent",
                table: "ChannelOperationRequestSignatures",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "IsTemplatePSBT",
                table: "ChannelOperationRequestSignatures",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UserSignerId",
                table: "ChannelOperationRequestSignatures",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AmountCryptoUnit",
                table: "ChannelOperationRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "InternalWalletId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "InternalWallets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DerivationPath = table.Column<string>(type: "text", nullable: false),
                    MnemonicString = table.Column<string>(type: "text", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalWallets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_InternalWalletId",
                table: "Wallets",
                column: "InternalWalletId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequestSignatures_UserSignerId",
                table: "ChannelOperationRequestSignatures",
                column: "UserSignerId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_InternalWalletId",
                table: "ChannelOperationRequests",
                column: "InternalWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_InternalWallets_InternalWalletId",
                table: "ChannelOperationRequests",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequestSignatures_AspNetUsers_UserSignerId",
                table: "ChannelOperationRequestSignatures",
                column: "UserSignerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_InternalWallets_InternalWalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequestSignatures_AspNetUsers_UserSignerId",
                table: "ChannelOperationRequestSignatures");

            migrationBuilder.DropForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys");

            migrationBuilder.DropForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets");

            migrationBuilder.DropTable(
                name: "InternalWallets");

            migrationBuilder.DropIndex(
                name: "IX_Wallets_InternalWalletId",
                table: "Wallets");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequestSignatures_UserSignerId",
                table: "ChannelOperationRequestSignatures");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequests_InternalWalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "InternalWalletId",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "IsFundsManagerPrivateKey",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "SatsAmount",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "IsTemplatePSBT",
                table: "ChannelOperationRequestSignatures");

            migrationBuilder.DropColumn(
                name: "UserSignerId",
                table: "ChannelOperationRequestSignatures");

            migrationBuilder.DropColumn(
                name: "InternalWalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Keys",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Capacity",
                table: "Channels",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<string>(
                name: "SignatureContent",
                table: "ChannelOperationRequestSignatures",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AmountCryptoUnit",
                table: "ChannelOperationRequests",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
