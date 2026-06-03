using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddInitialChannelFees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InitialChannelBaseFeeMsat",
                table: "ChannelOperationRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InitialChannelFeeRatePpm",
                table: "ChannelOperationRequests",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitialChannelBaseFeeMsat",
                table: "ChannelOperationRequests");

            migrationBuilder.DropColumn(
                name: "InitialChannelFeeRatePpm",
                table: "ChannelOperationRequests");
        }
    }
}
