using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class NodeReturningFundsMultisigWallet : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReturningFundsMultisigWalletId",
                table: "Nodes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ReturningFundsMultisigWalletId",
                table: "Nodes",
                column: "ReturningFundsMultisigWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsMultisigWalletId",
                table: "Nodes",
                column: "ReturningFundsMultisigWalletId",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_ReturningFundsMultisigWalletId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_ReturningFundsMultisigWalletId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ReturningFundsMultisigWalletId",
                table: "Nodes");
        }
    }
}
