using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddSwapOutDestinationAddressAndPaymentHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationAddress",
                table: "SwapOuts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentHash",
                table: "SwapOuts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinationAddress",
                table: "SwapOuts");

            migrationBuilder.DropColumn(
                name: "PaymentHash",
                table: "SwapOuts");
        }
    }
}
