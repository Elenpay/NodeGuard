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
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddSwapOuts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SwapOuts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    ProviderId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IsManual = table.Column<bool>(type: "boolean", nullable: false),
                    SatsAmount = table.Column<long>(type: "bigint", nullable: false),
                    DestinationWalletId = table.Column<int>(type: "integer", nullable: true),
                    ServiceFeeSats = table.Column<long>(type: "bigint", nullable: true),
                    LightningFeeSats = table.Column<long>(type: "bigint", nullable: true),
                    OnChainFeeSats = table.Column<long>(type: "bigint", nullable: true),
                    ErrorDetails = table.Column<string>(type: "text", nullable: true),
                    UserRequestorId = table.Column<string>(type: "text", nullable: true),
                    TxId = table.Column<string>(type: "text", nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwapOuts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwapOuts_AspNetUsers_UserRequestorId",
                        column: x => x.UserRequestorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SwapOuts_Wallets_DestinationWalletId",
                        column: x => x.DestinationWalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwapOuts_DestinationWalletId",
                table: "SwapOuts",
                column: "DestinationWalletId");

            migrationBuilder.CreateIndex(
                name: "IX_SwapOuts_UserRequestorId",
                table: "SwapOuts",
                column: "UserRequestorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwapOuts");
        }
    }
}
