using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class ChannelModelUpdates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Channels_ChannelId",
                table: "ChannelOperationRequests");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "ChannelOperationRequests",
                newName: "Status");

            migrationBuilder.AlterColumn<decimal>(
                name: "Capacity",
                table: "Channels",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "BtcCloseAddress",
                table: "Channels",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<int>(
                name: "ChannelId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "IsChannelPrivate",
                table: "ChannelOperationRequests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Channels_ChannelId",
                table: "ChannelOperationRequests",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Channels_ChannelId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "BtcCloseAddress",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "IsChannelPrivate",
                table: "ChannelOperationRequests");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "ChannelOperationRequests",
                newName: "status");

            migrationBuilder.AlterColumn<string>(
                name: "Capacity",
                table: "Channels",
                type: "text",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<int>(
                name: "ChannelId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Channels_ChannelId",
                table: "ChannelOperationRequests",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
