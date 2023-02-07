using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class NodeAddedToLiquidityRule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NodeId",
                table: "LiquidityRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_LiquidityRules_NodeId",
                table: "LiquidityRules",
                column: "NodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_LiquidityRules_Nodes_NodeId",
                table: "LiquidityRules",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiquidityRules_Nodes_NodeId",
                table: "LiquidityRules");

            migrationBuilder.DropIndex(
                name: "IX_LiquidityRules_NodeId",
                table: "LiquidityRules");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "LiquidityRules");
        }
    }
}
