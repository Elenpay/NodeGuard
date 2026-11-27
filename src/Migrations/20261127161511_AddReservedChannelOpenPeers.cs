using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddReservedChannelOpenPeers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReservedPeer",
                table: "ChannelOpenRecommendations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ReservedChannelOpenPeers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    PeerNodeId = table.Column<int>(type: "integer", nullable: false),
                    BudgetPct = table.Column<int>(type: "integer", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservedChannelOpenPeers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReservedChannelOpenPeers_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReservedChannelOpenPeers_Nodes_PeerNodeId",
                        column: x => x.PeerNodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReservedChannelOpenPeers_NodeId_PeerNodeId",
                table: "ReservedChannelOpenPeers",
                columns: new[] { "NodeId", "PeerNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReservedChannelOpenPeers_PeerNodeId",
                table: "ReservedChannelOpenPeers",
                column: "PeerNodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReservedChannelOpenPeers");

            migrationBuilder.DropColumn(
                name: "IsReservedPeer",
                table: "ChannelOpenRecommendations");
        }
    }
}
