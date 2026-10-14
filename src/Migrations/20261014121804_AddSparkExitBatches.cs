using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddSparkExitBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SparkExitDatetime",
                table: "SwapOuts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SparkExitDatetime",
                table: "SwapOuts");
        }
    }
}
