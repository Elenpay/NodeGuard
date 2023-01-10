using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class KeyFKInternalWallet : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFundsManagerPrivateKey",
                table: "Keys");

            migrationBuilder.AddColumn<int>(
                name: "InternalWalletId",
                table: "Keys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MasterFingerprint",
                table: "InternalWallets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Keys_InternalWalletId",
                table: "Keys",
                column: "InternalWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_InternalWallets_InternalWalletId",
                table: "Keys",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Keys_InternalWallets_InternalWalletId",
                table: "Keys");

            migrationBuilder.DropIndex(
                name: "IX_Keys_InternalWalletId",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "InternalWalletId",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "MasterFingerprint",
                table: "InternalWallets");

            migrationBuilder.AddColumn<bool>(
                name: "IsFundsManagerPrivateKey",
                table: "Keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
