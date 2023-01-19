using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class CreatedByField : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "Wallets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_CreatedBy",
                table: "Wallets",
                column: "CreatedBy");

            migrationBuilder.AddForeignKey(
                name: "FK_Wallets_AspNetUsers_CreatedBy",
                table: "Wallets",
                column: "CreatedBy",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Wallets_AspNetUsers_CreatedBy",
                table: "Wallets");

            migrationBuilder.DropIndex(
                name: "IX_Wallets_CreatedBy",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Wallets");
        }
    }
}
