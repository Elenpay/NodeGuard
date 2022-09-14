using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class keysExtraColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Path",
                table: "Keys",
                type: "text",
                nullable: false,
                defaultValue: ""
            );
            migrationBuilder.AddColumn<string>(
                name: "MasterFingerprint",
                table: "Keys",
                type: "text",
                nullable: false,
                defaultValue: ""
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MasterFingerprint",
                table: "Keys");
            migrationBuilder.DropColumn(
                name: "Path",
                table: "Keys");
        }
    }
}
