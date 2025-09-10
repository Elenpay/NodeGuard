using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeIdToSwapOutModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ProviderId",
                table: "SwapOuts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "NodeId",
                table: "SwapOuts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SwapOuts_NodeId",
                table: "SwapOuts",
                column: "NodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SwapOuts_Nodes_NodeId",
                table: "SwapOuts",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SwapOuts_Nodes_NodeId",
                table: "SwapOuts");

            migrationBuilder.DropIndex(
                name: "IX_SwapOuts_NodeId",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "SwapOuts");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderId",
                table: "SwapOuts",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
