using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class UTXO : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UTXOs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TxId = table.Column<string>(type: "text", nullable: false),
                    OutputIndex = table.Column<int>(type: "integer", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequestUTXO_UtxosId",
                table: "ChannelOperationRequestUTXO",
                column: "UtxosId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelOperationRequestUTXO");

            migrationBuilder.DropTable(
                name: "UTXOs");
        }
    }
}
