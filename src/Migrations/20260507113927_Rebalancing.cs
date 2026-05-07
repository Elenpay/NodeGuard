using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class Rebalancing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Rebalances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    SourceNodePubKey = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IsManual = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    RequestedAmountSats = table.Column<long>(type: "bigint", nullable: false),
                    SatsAmount = table.Column<long>(type: "bigint", nullable: false),
                    MaxFeePct = table.Column<double>(type: "double precision", nullable: false),
                    RetryMaxFeePct = table.Column<double>(type: "double precision", nullable: true),
                    FeePaidSats = table.Column<long>(type: "bigint", nullable: true),
                    FeePaidMsat = table.Column<long>(type: "bigint", nullable: true),
                    SourceChannelId = table.Column<int>(type: "integer", nullable: true),
                    SourceChanIdLnd = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    TargetPubkey = table.Column<string>(type: "text", nullable: true),
                    PreimageHex = table.Column<string>(type: "text", nullable: true),
                    UserRequestorId = table.Column<string>(type: "text", nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    ProbeBackoffRatio = table.Column<double>(type: "double precision", nullable: true),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rebalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rebalances_AspNetUsers_UserRequestorId",
                        column: x => x.UserRequestorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Rebalances_Channels_SourceChannelId",
                        column: x => x.SourceChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rebalances_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Rebalances_NodeId",
                table: "Rebalances",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Rebalances_SourceChannelId",
                table: "Rebalances",
                column: "SourceChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Rebalances_UserRequestorId",
                table: "Rebalances",
                column: "UserRequestorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Rebalances");
        }
    }
}
