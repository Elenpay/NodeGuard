using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class BIP39Import : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets");

            migrationBuilder.AlterColumn<int>(
                name: "InternalWalletId",
                table: "Wallets",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "IsBIP39Imported",
                table: "Wallets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsBIP39ImportedKey",
                table: "Keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "IsBIP39Imported",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "IsBIP39ImportedKey",
                table: "Keys");

            migrationBuilder.AlterColumn<int>(
                name: "InternalWalletId",
                table: "Wallets",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Wallets_InternalWallets_InternalWalletId",
                table: "Wallets",
                column: "InternalWalletId",
                principalTable: "InternalWallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
