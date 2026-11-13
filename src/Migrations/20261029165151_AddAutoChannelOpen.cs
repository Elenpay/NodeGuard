using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoChannelOpen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeSpan>(
                name: "AutoChannelOpenBudgetRefreshInterval",
                table: "Nodes",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AutoChannelOpenBudgetSats",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AutoChannelOpenBudgetStartDatetime",
                table: "Nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoChannelOpenEnabled",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoChannelOpenMode",
                table: "Nodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "AutoChannelOpenSizeSats",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoChannelOpenWalletId",
                table: "Nodes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelOpenRecommendations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    PeerPubKey = table.Column<string>(type: "character varying(66)", maxLength: 66, nullable: false),
                    EarnedFeeSats = table.Column<long>(type: "bigint", nullable: false),
                    SettledOutSats = table.Column<long>(type: "bigint", nullable: false),
                    Refusals = table.Column<int>(type: "integer", nullable: false),
                    UsableLocalSats = table.Column<long>(type: "bigint", nullable: false),
                    PeerCapacitySats = table.Column<long>(type: "bigint", nullable: false),
                    SuggestedCapacitySats = table.Column<long>(type: "bigint", nullable: false),
                    InheritedFeeRatePpm = table.Column<long>(type: "bigint", nullable: true),
                    InheritedBaseFeeMsat = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastEvidenceAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChannelOperationRequestId = table.Column<int>(type: "integer", nullable: true),
                    DismissReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOpenRecommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelOpenRecommendations_ChannelOperationRequests_Channel~",
                        column: x => x.ChannelOperationRequestId,
                        principalTable: "ChannelOperationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ChannelOpenRecommendations_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_AutoChannelOpenWalletId",
                table: "Nodes",
                column: "AutoChannelOpenWalletId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOpenRecommendations_ChannelOperationRequestId",
                table: "ChannelOpenRecommendations",
                column: "ChannelOperationRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOpenRecommendations_NodeId_PeerPubKey",
                table: "ChannelOpenRecommendations",
                columns: new[] { "NodeId", "PeerPubKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_AutoChannelOpenWalletId",
                table: "Nodes",
                column: "AutoChannelOpenWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_AutoChannelOpenWalletId",
                table: "Nodes");

            migrationBuilder.DropTable(
                name: "ChannelOpenRecommendations");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_AutoChannelOpenWalletId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenBudgetRefreshInterval",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenBudgetSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenBudgetStartDatetime",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenEnabled",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenMode",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenSizeSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenWalletId",
                table: "Nodes");
        }
    }
}
