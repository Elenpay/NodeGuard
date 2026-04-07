using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class HTLCForward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForwardingHtlcEvents",
                columns: table => new
                {
                    ManagedNodePubKey = table.Column<string>(type: "text", nullable: false),
                    IncomingChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    OutgoingChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    IncomingHtlcId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    OutgoingHtlcId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ManagedNodeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    EventCase = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    IncomingTimelock = table.Column<long>(type: "bigint", nullable: true),
                    OutgoingTimelock = table.Column<long>(type: "bigint", nullable: true),
                    IncomingAmountMsat = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    OutgoingAmountMsat = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    IncomingPeerAlias = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OutgoingPeerAlias = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FeeMsat = table.Column<long>(type: "bigint", nullable: true),
                    GrossFeeMsat = table.Column<long>(type: "bigint", nullable: true),
                    InboundFeeMsat = table.Column<long>(type: "bigint", nullable: true),
                    RoutingFeePpm = table.Column<long>(type: "bigint", nullable: true),
                    InboundFeePpm = table.Column<long>(type: "bigint", nullable: true),
                    WireFailureCode = table.Column<int>(type: "integer", nullable: true),
                    FailureDetail = table.Column<int>(type: "integer", nullable: true),
                    FailureString = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForwardingHtlcEvents", x => new { x.ManagedNodePubKey, x.IncomingChannelId, x.OutgoingChannelId, x.IncomingHtlcId, x.OutgoingHtlcId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForwardingHtlcEvents_CreationDatetime",
                table: "ForwardingHtlcEvents",
                column: "CreationDatetime");

            migrationBuilder.CreateIndex(
                name: "IX_ForwardingHtlcEvents_EventTimestamp",
                table: "ForwardingHtlcEvents",
                column: "EventTimestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForwardingHtlcEvents");
        }
    }
}
