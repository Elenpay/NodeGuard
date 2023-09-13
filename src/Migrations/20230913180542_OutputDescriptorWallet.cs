using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class OutputDescriptorWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportedOutputDescriptor",
                table: "Wallets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsUnSortedMultiSig",
                table: "Wallets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImportedOutputDescriptor",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "IsUnSortedMultiSig",
                table: "Wallets");
        }
    }
}
