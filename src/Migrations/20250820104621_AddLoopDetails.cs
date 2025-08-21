using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoopEndpoint",
                table: "Nodes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoopMacaroon",
                table: "Nodes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoopEndpoint",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "LoopMacaroon",
                table: "Nodes");
        }
    }
}
