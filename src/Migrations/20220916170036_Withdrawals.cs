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
    public partial class Withdrawals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalletWithdrawalRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DestinationAddress = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    WithdrawAllFunds = table.Column<bool>(type: "boolean", nullable: false),
                    RejectCancelDescription = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    JobId = table.Column<string>(type: "text", nullable: true),
                    TxId = table.Column<string>(type: "text", nullable: true),
                    UserRequestorId = table.Column<string>(type: "text", nullable: false),
                    WalletId = table.Column<int>(type: "integer", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletWithdrawalRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalletWithdrawalRequests_AspNetUsers_UserRequestorId",
                        column: x => x.UserRequestorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WalletWithdrawalRequests_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FMUTXOWalletWithdrawalRequest",
                columns: table => new
                {
                    UTXOsId = table.Column<int>(type: "integer", nullable: false),
                    WalletWithdrawalRequestsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FMUTXOWalletWithdrawalRequest", x => new { x.UTXOsId, x.WalletWithdrawalRequestsId });
                    table.ForeignKey(
                        name: "FK_FMUTXOWalletWithdrawalRequest_FMUTXOs_UTXOsId",
                        column: x => x.UTXOsId,
                        principalTable: "FMUTXOs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FMUTXOWalletWithdrawalRequest_WalletWithdrawalRequests_Wall~",
                        column: x => x.WalletWithdrawalRequestsId,
                        principalTable: "WalletWithdrawalRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalletWithdrawalRequestPSBTs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PSBT = table.Column<string>(type: "text", nullable: false),
                    IsInternalWalletPSBT = table.Column<bool>(type: "boolean", nullable: false),
                    IsTemplatePSBT = table.Column<bool>(type: "boolean", nullable: false),
                    IsFinalisedPSBT = table.Column<bool>(type: "boolean", nullable: false),
                    SignerId = table.Column<string>(type: "text", nullable: true),
                    WalletWithdrawalRequestId = table.Column<int>(type: "integer", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletWithdrawalRequestPSBTs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalletWithdrawalRequestPSBTs_AspNetUsers_SignerId",
                        column: x => x.SignerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WalletWithdrawalRequestPSBTs_WalletWithdrawalRequests_Walle~",
                        column: x => x.WalletWithdrawalRequestId,
                        principalTable: "WalletWithdrawalRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FMUTXOWalletWithdrawalRequest_WalletWithdrawalRequestsId",
                table: "FMUTXOWalletWithdrawalRequest",
                column: "WalletWithdrawalRequestsId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletWithdrawalRequestPSBTs_SignerId",
                table: "WalletWithdrawalRequestPSBTs",
                column: "SignerId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletWithdrawalRequestPSBTs_WalletWithdrawalRequestId",
                table: "WalletWithdrawalRequestPSBTs",
                column: "WalletWithdrawalRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletWithdrawalRequests_UserRequestorId",
                table: "WalletWithdrawalRequests",
                column: "UserRequestorId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletWithdrawalRequests_WalletId",
                table: "WalletWithdrawalRequests",
                column: "WalletId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FMUTXOWalletWithdrawalRequest");

            migrationBuilder.DropTable(
                name: "WalletWithdrawalRequestPSBTs");

            migrationBuilder.DropTable(
                name: "WalletWithdrawalRequests");
        }
    }
}
