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
    public partial class DestNodeNullableUnitRemoval : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "AmountCryptoUnit",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<int>(
                name: "DestNodeId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                table: "ChannelOperationRequests",
                column: "DestNodeId",
                principalTable: "Nodes",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<int>(
                name: "DestNodeId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AmountCryptoUnit",
                table: "ChannelOperationRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                table: "ChannelOperationRequests",
                column: "DestNodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
