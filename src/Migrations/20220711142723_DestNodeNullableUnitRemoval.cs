using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class DestNodeNullableUnitRemoval : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "AmountCryptoUnit",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<int>(
                name: "DestNodeId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                table: "ChannelOperationRequests",
                column: "DestNodeId",
                principalTable: "Nodes",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<int>(
                name: "DestNodeId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AmountCryptoUnit",
                table: "ChannelOperationRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                table: "ChannelOperationRequests",
                column: "DestNodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
