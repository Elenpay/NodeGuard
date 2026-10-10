using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class DropDistinctSourcePeersFromChannelOpenRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DistinctSourcePeers",
                table: "ChannelOpenRecommendations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DistinctSourcePeers",
                table: "ChannelOpenRecommendations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
