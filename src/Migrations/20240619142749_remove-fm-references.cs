using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class removefmreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelOperationRequestFMUTXO");

            migrationBuilder.DropTable(
                name: "FMUTXOWalletWithdrawalRequest");

            migrationBuilder.DropTable(
                name: "FMUTXOs");

            migrationBuilder.CreateTable(
                name: "UTXOs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TxId = table.Column<string>(type: "text", nullable: false),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: false),
                    SatsAmount = table.Column<long>(type: "bigint", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UTXOs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelOperationRequestUTXO",
                columns: table => new
                {
                    ChannelOperationRequestsId = table.Column<int>(type: "integer", nullable: false),
                    UtxosId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOperationRequestUTXO", x => new { x.ChannelOperationRequestsId, x.UtxosId });
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequestUTXO_ChannelOperationRequests_Channe~",
                        column: x => x.ChannelOperationRequestsId,
                        principalTable: "ChannelOperationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequestUTXO_UTXOs_UtxosId",
                        column: x => x.UtxosId,
                        principalTable: "UTXOs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UTXOWalletWithdrawalRequest",
                columns: table => new
                {
                    UTXOsId = table.Column<int>(type: "integer", nullable: false),
                    WalletWithdrawalRequestsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UTXOWalletWithdrawalRequest", x => new { x.UTXOsId, x.WalletWithdrawalRequestsId });
                    table.ForeignKey(
                        name: "FK_UTXOWalletWithdrawalRequest_UTXOs_UTXOsId",
                        column: x => x.UTXOsId,
                        principalTable: "UTXOs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UTXOWalletWithdrawalRequest_WalletWithdrawalRequests_Wallet~",
                        column: x => x.WalletWithdrawalRequestsId,
                        principalTable: "WalletWithdrawalRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequestUTXO_UtxosId",
                table: "ChannelOperationRequestUTXO",
                column: "UtxosId");

            migrationBuilder.CreateIndex(
                name: "IX_UTXOWalletWithdrawalRequest_WalletWithdrawalRequestsId",
                table: "UTXOWalletWithdrawalRequest",
                column: "WalletWithdrawalRequestsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelOperationRequestUTXO");

            migrationBuilder.DropTable(
                name: "UTXOWalletWithdrawalRequest");

            migrationBuilder.DropTable(
                name: "UTXOs");

            migrationBuilder.CreateTable(
                name: "FMUTXOs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: false),
                    SatsAmount = table.Column<long>(type: "bigint", nullable: false),
                    TxId = table.Column<string>(type: "text", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FMUTXOs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelOperationRequestFMUTXO",
                columns: table => new
                {
                    ChannelOperationRequestsId = table.Column<int>(type: "integer", nullable: false),
                    UtxosId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOperationRequestFMUTXO", x => new { x.ChannelOperationRequestsId, x.UtxosId });
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequestFMUTXO_ChannelOperationRequests_Chan~",
                        column: x => x.ChannelOperationRequestsId,
                        principalTable: "ChannelOperationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequestFMUTXO_FMUTXOs_UtxosId",
                        column: x => x.UtxosId,
                        principalTable: "FMUTXOs",
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

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequestFMUTXO_UtxosId",
                table: "ChannelOperationRequestFMUTXO",
                column: "UtxosId");

            migrationBuilder.CreateIndex(
                name: "IX_FMUTXOWalletWithdrawalRequest_WalletWithdrawalRequestsId",
                table: "FMUTXOWalletWithdrawalRequest",
                column: "WalletWithdrawalRequestsId");
        }
    }
}
