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
    public partial class AddNodeIdToSwapOutModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ProviderId",
                table: "SwapOuts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "NodeId",
                table: "SwapOuts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SwapOuts_NodeId",
                table: "SwapOuts",
                column: "NodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SwapOuts_Nodes_NodeId",
                table: "SwapOuts",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SwapOuts_Nodes_NodeId",
                table: "SwapOuts");

            migrationBuilder.DropIndex(
                name: "IX_SwapOuts_NodeId",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "SwapOuts");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderId",
                table: "SwapOuts",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
