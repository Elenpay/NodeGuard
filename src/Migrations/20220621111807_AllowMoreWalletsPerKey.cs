using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class AllowMoreWalletsPerKey : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Keys_Wallets_WalletId1",
                table: "Keys");

            migrationBuilder.DropIndex(
                name: "IX_Keys_WalletId1",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "WalletId",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "WalletId1",
                table: "Keys");

            migrationBuilder.CreateTable(
                name: "KeyWallet",
                columns: table => new
                {
                    KeysId = table.Column<int>(type: "integer", nullable: false),
                    WalletsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyWallet", x => new { x.KeysId, x.WalletsId });
                    table.ForeignKey(
                        name: "FK_KeyWallet_Keys_KeysId",
                        column: x => x.KeysId,
                        principalTable: "Keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KeyWallet_Wallets_WalletsId",
                        column: x => x.WalletsId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeyWallet_WalletsId",
                table: "KeyWallet",
                column: "WalletsId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KeyWallet");

            migrationBuilder.AddColumn<string>(
                name: "WalletId",
                table: "Keys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalletId1",
                table: "Keys",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Keys_WalletId1",
                table: "Keys",
                column: "WalletId1");

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_Wallets_WalletId1",
                table: "Keys",
                column: "WalletId1",
                principalTable: "Wallets",
                principalColumn: "Id");
        }
    }
}
