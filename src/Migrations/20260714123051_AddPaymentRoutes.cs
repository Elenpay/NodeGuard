using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentRoutes",
                columns: table => new
                {
                    PaymentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OriginNodePubKey = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: true),
                    Destination = table.Column<string>(type: "text", nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRoutes", x => x.PaymentHash);
                });

            migrationBuilder.CreateTable(
                name: "PaymentRouteHops",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PaymentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AttemptIndex = table.Column<int>(type: "integer", nullable: false),
                    HopSequence = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    FromNode = table.Column<string>(type: "text", nullable: false),
                    ToNode = table.Column<string>(type: "text", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRouteHops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRouteHops_PaymentRoutes_PaymentHash",
                        column: x => x.PaymentHash,
                        principalTable: "PaymentRoutes",
                        principalColumn: "PaymentHash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRouteHops_PaymentHash",
                table: "PaymentRouteHops",
                column: "PaymentHash");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRoutes_CreatedAt",
                table: "PaymentRoutes",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentRouteHops");

            migrationBuilder.DropTable(
                name: "PaymentRoutes");
        }
    }
}
