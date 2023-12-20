using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class WithdrawalFeeRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MempoolRecommendedFeesTypes",
                table: "ChannelOperationRequests",
                newName: "MempoolRecommendedFeesType");

            migrationBuilder.AddColumn<decimal>(
                name: "CustomFeeRate",
                table: "WalletWithdrawalRequests",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MempoolRecommendedFeesType",
                table: "WalletWithdrawalRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomFeeRate",
                table: "WalletWithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "MempoolRecommendedFeesType",
                table: "WalletWithdrawalRequests");

            migrationBuilder.RenameColumn(
                name: "MempoolRecommendedFeesType",
                table: "ChannelOperationRequests",
                newName: "MempoolRecommendedFeesTypes");
        }
    }
}
