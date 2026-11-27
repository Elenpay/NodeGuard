using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddSwapOutReferenceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferenceId",
                table: "SwapOuts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SwapOuts_ReferenceId",
                table: "SwapOuts",
                column: "ReferenceId",
                unique: true,
                filter: "\"ReferenceId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SwapOuts_ReferenceId",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "ReferenceId",
                table: "SwapOuts");
        }
    }
}
