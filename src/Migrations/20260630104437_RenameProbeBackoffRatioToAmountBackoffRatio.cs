using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class RenameProbeBackoffRatioToAmountBackoffRatio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProbeBackoffRatio",
                table: "Rebalances",
                newName: "AmountBackoffRatio");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AmountBackoffRatio",
                table: "Rebalances",
                newName: "ProbeBackoffRatio");
        }
    }
}
