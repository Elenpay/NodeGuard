using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class UpdatesAfterCodeReview : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Channels_ChannelId1",
                table: "ChannelOperationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Wallets_WalletId1",
                table: "ChannelOperationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequestSignatures_ChannelOperationRequests_~",
                table: "ChannelOperationRequestSignatures");

            migrationBuilder.DropForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequests_ChannelId1",
                table: "ChannelOperationRequests");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequests_WalletId1",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "ChannelId1",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "WalletId1",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<int>(
                name: "MofN",
                table: "Wallets",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Keys",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ChannelOperationRequestId",
                table: "ChannelOperationRequestSignatures",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ChannelOpenRequestId",
                table: "ChannelOperationRequestSignatures",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "WalletId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ChannelId",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_ChannelId",
                table: "ChannelOperationRequests",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_WalletId",
                table: "ChannelOperationRequests",
                column: "WalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Channels_ChannelId",
                table: "ChannelOperationRequests",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Wallets_WalletId",
                table: "ChannelOperationRequests",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequestSignatures_ChannelOperationRequests_~",
                table: "ChannelOperationRequestSignatures",
                column: "ChannelOperationRequestId",
                principalTable: "ChannelOperationRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Channels_ChannelId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequests_Wallets_WalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequestSignatures_ChannelOperationRequests_~",
                table: "ChannelOperationRequestSignatures");

            migrationBuilder.DropForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequests_ChannelId",
                table: "ChannelOperationRequests");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequests_WalletId",
                table: "ChannelOperationRequests");

            migrationBuilder.AlterColumn<string>(
                name: "MofN",
                table: "Wallets",
                type: "text",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Keys",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<int>(
                name: "ChannelOperationRequestId",
                table: "ChannelOperationRequestSignatures",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "ChannelOpenRequestId",
                table: "ChannelOperationRequestSignatures",
                type: "text",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "WalletId",
                table: "ChannelOperationRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "ChannelId",
                table: "ChannelOperationRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "ChannelId1",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalletId1",
                table: "ChannelOperationRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_ChannelId1",
                table: "ChannelOperationRequests",
                column: "ChannelId1");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_WalletId1",
                table: "ChannelOperationRequests",
                column: "WalletId1");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Channels_ChannelId1",
                table: "ChannelOperationRequests",
                column: "ChannelId1",
                principalTable: "Channels",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequests_Wallets_WalletId1",
                table: "ChannelOperationRequests",
                column: "WalletId1",
                principalTable: "Wallets",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequestSignatures_ChannelOperationRequests_~",
                table: "ChannelOperationRequestSignatures",
                column: "ChannelOperationRequestId",
                principalTable: "ChannelOperationRequests",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }
    }
}
