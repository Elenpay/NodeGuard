using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddSparkSwapProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SparkIdentity",
                table: "SwapOuts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SparkTransferId",
                table: "SwapOuts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SparkLeafIds",
                table: "SwapOuts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SparkReceivedSats",
                table: "SwapOuts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PayoutSats",
                table: "SwapOuts",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayoutSats",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "SparkReceivedSats",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "SparkLeafIds",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "SparkTransferId",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "SparkIdentity",
                table: "SwapOuts");
        }
    }
}
