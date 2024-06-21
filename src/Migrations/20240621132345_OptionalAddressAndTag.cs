using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class OptionalAddressAndTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FMUTXOs_UTXOTag_TagId",
                table: "FMUTXOs");

            migrationBuilder.AlterColumn<int>(
                name: "TagId",
                table: "FMUTXOs",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "Address",
                table: "FMUTXOs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddForeignKey(
                name: "FK_FMUTXOs_UTXOTag_TagId",
                table: "FMUTXOs",
                column: "TagId",
                principalTable: "UTXOTag",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FMUTXOs_UTXOTag_TagId",
                table: "FMUTXOs");

            migrationBuilder.AlterColumn<int>(
                name: "TagId",
                table: "FMUTXOs",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Address",
                table: "FMUTXOs",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FMUTXOs_UTXOTag_TagId",
                table: "FMUTXOs",
                column: "TagId",
                principalTable: "UTXOTag",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
