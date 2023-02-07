using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class ChannelNode : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NodeId",
                table: "Channels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_NodeId",
                table: "Channels",
                column: "NodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Nodes_NodeId",
                table: "Channels",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Nodes_NodeId",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_Channels_NodeId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "Channels");
        }
    }
}
