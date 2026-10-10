using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddSparkWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Wallets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SparkAccount",
                table: "Wallets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SparkEncryptedMnemonic",
                table: "Wallets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SparkIdentityPublicKey",
                table: "Wallets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SparkMaxBalanceSats",
                table: "Wallets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SparkWalletId",
                table: "Nodes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_SparkWalletId",
                table: "Nodes",
                column: "SparkWalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Wallets_SparkWalletId",
                table: "Nodes",
                column: "SparkWalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Wallets_SparkWalletId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_SparkWalletId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "SparkAccount",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "SparkEncryptedMnemonic",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "SparkIdentityPublicKey",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "SparkMaxBalanceSats",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "SparkWalletId",
                table: "Nodes");
        }
    }
}
