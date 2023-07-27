using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    public partial class RenameReturnWalletNode : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsMultisigWalletId",
                table: "Nodes");

            migrationBuilder.RenameColumn(
                name: "ReturningFundsMultisigWalletId",
                table: "Nodes",
                newName: "ReturningFundsWalletId");

            migrationBuilder.RenameIndex(
                name: "IX_Nodes_ReturningFundsMultisigWalletId",
                table: "Nodes",
                newName: "IX_Nodes_ReturningFundsWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsWalletId",
                table: "Nodes",
                column: "ReturningFundsWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsWalletId",
                table: "Nodes");

            migrationBuilder.RenameColumn(
                name: "ReturningFundsWalletId",
                table: "Nodes",
                newName: "ReturningFundsMultisigWalletId");

            migrationBuilder.RenameIndex(
                name: "IX_Nodes_ReturningFundsWalletId",
                table: "Nodes",
                newName: "IX_Nodes_ReturningFundsMultisigWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsMultisigWalletId",
                table: "Nodes",
                column: "ReturningFundsMultisigWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }
    }
}
