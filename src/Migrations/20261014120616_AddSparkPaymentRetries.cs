using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddSparkPaymentRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SparkInvoiceExpiresAt",
                table: "SwapOuts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SparkPaymentAttempt",
                table: "SwapOuts",
                type: "integer",
                nullable: false,
                // Every swap recorded so far is on its first attempt
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SparkInvoiceExpiresAt",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "SparkPaymentAttempt",
                table: "SwapOuts");
        }
    }
}
