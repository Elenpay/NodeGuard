using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class LiquidityRulesEnabledTarget : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "LiquidityRules");

            migrationBuilder.AddColumn<decimal>(
                name: "RebalanceTarget",
                table: "LiquidityRules",
                type: "numeric",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RebalanceTarget",
                table: "LiquidityRules");

            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "LiquidityRules",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
