using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddPerProviderSwapAmountsAndSparkExitBatches : Migration
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

            migrationBuilder.AddColumn<long>(
                name: "FortySwapMinAmountSats",
                table: "Nodes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // The node's shared swap sizes become Loop's (the rename above) and 40swap's too
            migrationBuilder.Sql(
                "UPDATE \"Nodes\" SET \"FortySwapMinAmountSats\" = \"LoopSwapMinAmountSats\", \"FortySwapMaxAmountSats\" = \"LoopSwapMaxAmountSats\";");

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
                name: "FortySwapMinAmountSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SparkSwapMaxAmountSats",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SparkSwapMinAmountSats",
                table: "Nodes");

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
