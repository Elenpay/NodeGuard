using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    public partial class AddConstraintsAndPopulateMasterFingerprint : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Wallets_InternalWalletSubDerivationPath_InternalWalletMaste~",
                table: "Wallets",
                columns: new[] { "InternalWalletSubDerivationPath", "InternalWalletMasterFingerprint" },
                unique: true);
            migrationBuilder.Sql("UPDATE \"Wallets\" as w SET \"InternalWalletMasterFingerprint\" = iw.\"MasterFingerprint\" FROM (SELECT * FROM \"InternalWallets\" LIMIT 1) as iw");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Wallets_InternalWalletSubDerivationPath_InternalWalletMaste~",
                table: "Wallets");
        }
    }
}
