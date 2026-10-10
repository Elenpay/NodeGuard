using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelOpenRecommendation : Migration
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

            migrationBuilder.AddColumn<long>(
                name: "AutoChannelOpenMaxSizeSats",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxChannelOpenCostToEarnRatio",
                table: "Nodes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AutoChannelOpenMinSizeSats",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoChannelOpenMode",
                table: "Nodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

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
                    PeerAlias = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OutgoingChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ChannelsInvolved = table.Column<int>(type: "integer", nullable: false),
                    Bursts = table.Column<int>(type: "integer", nullable: false),
                    DistinctSourcePeers = table.Column<int>(type: "integer", nullable: false),
                    MissedFeeMsat = table.Column<long>(type: "bigint", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    OutSats = table.Column<long>(type: "bigint", nullable: false),
                    InSats = table.Column<long>(type: "bigint", nullable: false),
                    MissedSats = table.Column<long>(type: "bigint", nullable: false),
                    MaxBurstPaymentSats = table.Column<long>(type: "bigint", nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    SuggestedCapacitySats = table.Column<long>(type: "bigint", nullable: false),
                    BindingClamp = table.Column<int>(type: "integer", nullable: false),
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
                name: "IX_ForwardingHtlcEvents_ManagedNodePubKey_EventTimestamp",
                table: "ForwardingHtlcEvents",
                columns: new[] { "ManagedNodePubKey", "EventTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOpenRecommendations_ChannelOperationRequestId",
                table: "ChannelOpenRecommendations",
                column: "ChannelOperationRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOpenRecommendations_NodeId_PeerPubKey",
                table: "ChannelOpenRecommendations",
                columns: new[] { "NodeId", "PeerPubKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOpenRecommendations_NodeId_Status",
                table: "ChannelOpenRecommendations",
                columns: new[] { "NodeId", "Status" });

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

            migrationBuilder.DropIndex(
                name: "IX_ForwardingHtlcEvents_ManagedNodePubKey_EventTimestamp",
                table: "ForwardingHtlcEvents");

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
                name: "AutoChannelOpenMaxSizeSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MaxChannelOpenCostToEarnRatio",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenMinSizeSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenMode",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoChannelOpenWalletId",
                table: "Nodes");
        }
    }
}
