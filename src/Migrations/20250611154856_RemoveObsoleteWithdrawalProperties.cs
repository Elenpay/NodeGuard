using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class RemoveObsoleteWithdrawalProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Amount",
                table: "WalletWithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "DestinationAddress",
                table: "WalletWithdrawalRequests");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "WalletWithdrawalRequests",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "DestinationAddress",
                table: "WalletWithdrawalRequests",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
