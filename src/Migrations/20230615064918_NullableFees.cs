using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    public partial class NullableFees : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "feeRate",
                table: "ChannelOperationRequests",
                newName: "FeeRate");

            migrationBuilder.AlterColumn<decimal>(
                name: "FeeRate",
                table: "ChannelOperationRequests",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FeeRate",
                table: "ChannelOperationRequests",
                newName: "feeRate");

            migrationBuilder.AlterColumn<long>(
                name: "feeRate",
                table: "ChannelOperationRequests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);
        }
    }
}
