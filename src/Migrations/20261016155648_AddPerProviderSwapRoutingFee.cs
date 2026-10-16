using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddPerProviderSwapRoutingFee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MaxSwapRoutingFeeRatio",
                table: "Nodes",
                newName: "LoopMaxSwapRoutingFeeRatio");

            migrationBuilder.AddColumn<decimal>(
                name: "FortySwapMaxSwapRoutingFeeRatio",
                table: "Nodes",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SparkMaxSwapRoutingFeeRatio",
                table: "Nodes",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            // The node's shared max swap routing fee becomes Loop's (the rename above), and 40swap's and Spark's too
            migrationBuilder.Sql(
                "UPDATE \"Nodes\" SET \"FortySwapMaxSwapRoutingFeeRatio\" = \"LoopMaxSwapRoutingFeeRatio\", \"SparkMaxSwapRoutingFeeRatio\" = \"LoopMaxSwapRoutingFeeRatio\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FortySwapMaxSwapRoutingFeeRatio",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SparkMaxSwapRoutingFeeRatio",
                table: "Nodes");

            migrationBuilder.RenameColumn(
                name: "LoopMaxSwapRoutingFeeRatio",
                table: "Nodes",
                newName: "MaxSwapRoutingFeeRatio");
        }
    }
}
