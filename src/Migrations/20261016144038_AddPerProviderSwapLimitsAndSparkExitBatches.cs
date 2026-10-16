using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddPerProviderSwapLimitsAndSparkExitBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SwapMinAmountSats",
                table: "Nodes",
                newName: "LoopSwapMinAmountSats");

            migrationBuilder.RenameColumn(
                name: "SwapMaxAmountSats",
                table: "Nodes",
                newName: "LoopSwapMaxAmountSats");

            migrationBuilder.RenameColumn(
                name: "MaxSwapsInFlight",
                table: "Nodes",
                newName: "LoopMaxSwapsInFlight");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SparkExitDatetime",
                table: "SwapOuts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FortySwapMaxAmountSats",
                table: "Nodes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "FortySwapMaxSwapsInFlight",
                table: "Nodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "FortySwapMinAmountSats",
                table: "Nodes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "SparkMaxSwapsInFlight",
                table: "Nodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "SparkSwapMaxAmountSats",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SparkSwapMinAmountSats",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            // The node's shared swap sizes and max swaps in flight become Loop's (the renames above), and 40swap's
            // and Spark's max swaps in flight too; Spark's sizes default to SPARK_SWAP_MIN_SATS / SPARK_SWAP_MAX_SATS
            migrationBuilder.Sql(
                "UPDATE \"Nodes\" SET \"FortySwapMinAmountSats\" = \"LoopSwapMinAmountSats\", \"FortySwapMaxAmountSats\" = \"LoopSwapMaxAmountSats\", " +
                "\"FortySwapMaxSwapsInFlight\" = \"LoopMaxSwapsInFlight\", \"SparkMaxSwapsInFlight\" = \"LoopMaxSwapsInFlight\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SparkExitDatetime",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "FortySwapMaxAmountSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "FortySwapMaxSwapsInFlight",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "FortySwapMinAmountSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SparkMaxSwapsInFlight",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SparkSwapMaxAmountSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SparkSwapMinAmountSats",
                table: "Nodes");

            migrationBuilder.RenameColumn(
                name: "LoopMaxSwapsInFlight",
                table: "Nodes",
                newName: "MaxSwapsInFlight");

            migrationBuilder.RenameColumn(
                name: "LoopSwapMinAmountSats",
                table: "Nodes",
                newName: "SwapMinAmountSats");

            migrationBuilder.RenameColumn(
                name: "LoopSwapMaxAmountSats",
                table: "Nodes",
                newName: "SwapMaxAmountSats");
        }
    }
}
