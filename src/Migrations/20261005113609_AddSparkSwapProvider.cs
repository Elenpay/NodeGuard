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

            migrationBuilder.CreateIndex(
                name: "IX_SwapOuts_SingleSparkSwapInFlight",
                table: "SwapOuts",
                column: "Provider",
                unique: true,
                filter: "\"Provider\" = 2 AND \"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SwapOuts_SingleSparkSwapInFlight",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "SparkIdentity",
                table: "SwapOuts");
        }
    }
}
