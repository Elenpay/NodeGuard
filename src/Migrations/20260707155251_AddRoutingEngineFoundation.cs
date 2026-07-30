using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddRoutingEngineFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowPositiveInboundFees",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoRebalanceEnabled",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DynamicFeeManagementEnabled",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MaxRebalanceCostToEarnRatio",
                table: "Nodes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRebalancesInFlight",
                table: "Nodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "RebalanceBudgetRefreshInterval",
                table: "Nodes",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RebalanceBudgetSats",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RebalanceBudgetStartDatetime",
                table: "Nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RoutingEngineDryRun",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDynamicFeeEnabled",
                table: "Channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChannelFeeStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<int>(type: "integer", nullable: false),
                    LastFeeUpdateAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAppliedOutboundBaseFeeMsat = table.Column<long>(type: "bigint", nullable: true),
                    LastAppliedOutboundPpm = table.Column<long>(type: "bigint", nullable: true),
                    LastAppliedInboundBaseMsat = table.Column<int>(type: "integer", nullable: true),
                    LastAppliedInboundPpm = table.Column<int>(type: "integer", nullable: true),
                    LastComputedTarget = table.Column<double>(type: "double precision", nullable: true),
                    LastObservedRatio = table.Column<double>(type: "double precision", nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelFeeStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelFeeStates_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChannelRoutingStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<int>(type: "integer", nullable: false),
                    ChanIdLnd = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ManagedNodePubKey = table.Column<string>(type: "text", nullable: false),
                    TargetLocalRatio = table.Column<double>(type: "double precision", nullable: false),
                    PeerFlowCategory = table.Column<int>(type: "integer", nullable: false),
                    PendingCategory = table.Column<int>(type: "integer", nullable: true),
                    ConsecutiveCategoryCyclesInNewState = table.Column<long>(type: "bigint", nullable: false),
                    FundingBlockHeight = table.Column<long>(type: "bigint", nullable: true),
                    AgeBlocks = table.Column<long>(type: "bigint", nullable: true),
                    EmaLocalRatio = table.Column<double>(type: "double precision", nullable: false),
                    PushMsatWindow = table.Column<long>(type: "bigint", nullable: false),
                    PullMsatWindow = table.Column<long>(type: "bigint", nullable: false),
                    NetFlowRatio = table.Column<double>(type: "double precision", nullable: false),
                    PeerInitiated = table.Column<bool>(type: "boolean", nullable: false),
                    LastKnownNumUpdates = table.Column<long>(type: "bigint", nullable: true),
                    LastKnownLifetime = table.Column<long>(type: "bigint", nullable: true),
                    LastKnownUptime = table.Column<long>(type: "bigint", nullable: true),
                    LastCategorizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelRoutingStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelRoutingStates_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelFeeStates_ChannelId",
                table: "ChannelFeeStates",
                column: "ChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelRoutingStates_ChannelId",
                table: "ChannelRoutingStates",
                column: "ChannelId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelFeeStates");

            migrationBuilder.DropTable(
                name: "ChannelRoutingStates");

            migrationBuilder.DropColumn(
                name: "AllowPositiveInboundFees",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "AutoRebalanceEnabled",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "DynamicFeeManagementEnabled",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MaxRebalanceCostToEarnRatio",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MaxRebalancesInFlight",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "RebalanceBudgetRefreshInterval",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "RebalanceBudgetSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "RebalanceBudgetStartDatetime",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "RoutingEngineDryRun",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "IsDynamicFeeEnabled",
                table: "Channels");
        }
    }
}
