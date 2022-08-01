using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class ChannelOperationRequestSignatureOverhaul : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelOperationRequestSignatures");

            migrationBuilder.CreateTable(
                name: "OperationRequestPsbts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PSBT = table.Column<string>(type: "text", nullable: false),
                    IsTemplatePSBT = table.Column<bool>(type: "boolean", nullable: false),
                    IsInternalWalletPSBT = table.Column<bool>(type: "boolean", nullable: false),
                    IsFinalisedPSBT = table.Column<bool>(type: "boolean", nullable: false),
                    ChannelOperationRequestId = table.Column<int>(type: "integer", nullable: false),
                    UserSignerId = table.Column<string>(type: "text", nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationRequestPsbts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationRequestPsbts_AspNetUsers_UserSignerId",
                        column: x => x.UserSignerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OperationRequestPsbts_ChannelOperationRequests_ChannelOpera~",
                        column: x => x.ChannelOperationRequestId,
                        principalTable: "ChannelOperationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationRequestPsbts_ChannelOperationRequestId",
                table: "OperationRequestPsbts",
                column: "ChannelOperationRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationRequestPsbts_UserSignerId",
                table: "OperationRequestPsbts",
                column: "UserSignerId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationRequestPsbts");

            migrationBuilder.CreateTable(
                name: "ChannelOperationRequestSignatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelOperationRequestId = table.Column<int>(type: "integer", nullable: false),
                    UserSignerId = table.Column<string>(type: "text", nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsTemplatePSBT = table.Column<bool>(type: "boolean", nullable: false),
                    PSBT = table.Column<string>(type: "text", nullable: false),
                    SignatureContent = table.Column<string>(type: "text", nullable: true),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOperationRequestSignatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequestSignatures_AspNetUsers_UserSignerId",
                        column: x => x.UserSignerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequestSignatures_ChannelOperationRequests_~",
                        column: x => x.ChannelOperationRequestId,
                        principalTable: "ChannelOperationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequestSignatures_ChannelOperationRequestId",
                table: "ChannelOperationRequestSignatures",
                column: "ChannelOperationRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequestSignatures_UserSignerId",
                table: "ChannelOperationRequestSignatures",
                column: "UserSignerId");
        }
    }
}
