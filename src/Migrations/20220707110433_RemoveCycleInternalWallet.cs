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
    public partial class RemoveCycleInternalWallet : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_InternalWallets_InternalWalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequests_InternalWalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "InternalWalletId",
                table: "ChannelOperationRequests");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InternalWalletId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

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
        }
    }
}
