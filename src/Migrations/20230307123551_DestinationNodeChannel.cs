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
    public partial class DestinationNodeChannel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Nodes_NodeId",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "NodeId",
                table: "Channels",
                newName: "SourceNodeId");

            migrationBuilder.RenameIndex(
                name: "IX_Channels_NodeId",
                table: "Channels",
                newName: "IX_Channels_SourceNodeId");

            migrationBuilder.AddColumn<int>(
                name: "DestinationNodeId",
                table: "Channels",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_DestinationNodeId",
                table: "Channels",
                column: "DestinationNodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Nodes_DestinationNodeId",
                table: "Channels",
                column: "DestinationNodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Nodes_SourceNodeId",
                table: "Channels",
                column: "SourceNodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Nodes_DestinationNodeId",
                table: "Channels");

            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Nodes_SourceNodeId",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_Channels_DestinationNodeId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "DestinationNodeId",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "SourceNodeId",
                table: "Channels",
                newName: "NodeId");

            migrationBuilder.RenameIndex(
                name: "IX_Channels_SourceNodeId",
                table: "Channels",
                newName: "IX_Channels_NodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Nodes_NodeId",
                table: "Channels",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
