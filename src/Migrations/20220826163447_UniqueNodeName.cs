using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class UniqueNodeName : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Name",
                table: "Nodes",
                column: "Name",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Nodes_Name",
                table: "Nodes");
        }
    }
}
