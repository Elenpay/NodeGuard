using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddressInLiquidityRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "LiquidityRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsWalletRule",
                table: "LiquidityRules",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Address",
                table: "LiquidityRules");

            migrationBuilder.DropColumn(
                name: "IsWalletRule",
                table: "LiquidityRules");
        }
    }
}
