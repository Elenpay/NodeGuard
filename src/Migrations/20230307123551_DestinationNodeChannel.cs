using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class DestinationNodeChannel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Nodes_NodeId",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "NodeId",
                table: "Channels",
                newName: "SourceNodeId");

            migrationBuilder.RenameIndex(
                name: "IX_Channels_NodeId",
                table: "Channels",
                newName: "IX_Channels_SourceNodeId");

            migrationBuilder.AddColumn<int>(
                name: "DestinationNodeId",
                table: "Channels",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_DestinationNodeId",
                table: "Channels",
                column: "DestinationNodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Nodes_DestinationNodeId",
                table: "Channels",
                column: "DestinationNodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Nodes_SourceNodeId",
                table: "Channels",
                column: "SourceNodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Nodes_DestinationNodeId",
                table: "Channels");

            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Nodes_SourceNodeId",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_Channels_DestinationNodeId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "DestinationNodeId",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "SourceNodeId",
                table: "Channels",
                newName: "NodeId");

            migrationBuilder.RenameIndex(
                name: "IX_Channels_SourceNodeId",
                table: "Channels",
                newName: "IX_Channels_NodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Nodes_NodeId",
                table: "Channels",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
