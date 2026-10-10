using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddInheritedFeePolicyToChannelOpenRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InheritedBaseFeeMsat",
                table: "ChannelOpenRecommendations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InheritedFeeRatePpm",
                table: "ChannelOpenRecommendations",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InheritedBaseFeeMsat",
                table: "ChannelOpenRecommendations");

            migrationBuilder.DropColumn(
                name: "InheritedFeeRatePpm",
                table: "ChannelOpenRecommendations");
        }
    }
}
