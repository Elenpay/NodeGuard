using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class IsNodeDisabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsNodeDisabled",
                table: "Nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsNodeDisabled",
                table: "Nodes");
        }
    }
}
