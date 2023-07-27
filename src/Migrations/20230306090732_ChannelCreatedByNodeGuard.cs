using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    public partial class ChannelCreatedByNodeGuard : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreatedByNodeGuard",
                table: "Channels",
                type: "boolean",
                nullable: false,
                defaultValue: true);
            migrationBuilder.Sql("UPDATE \"Channels\" SET \"CreatedByNodeGuard\" = 'true'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByNodeGuard",
                table: "Channels");
        }
    }
}
