using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NodeGuard.Helpers;

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

            migrationBuilder.AlterColumn<List<ChannelStatusLog>>(
                name: "StatusLogs",
                table: "ChannelOperationRequests",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(List<ChannelStatusLog>),
                oldType: "jsonb");
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

            migrationBuilder.AlterColumn<List<ChannelStatusLog>>(
                name: "StatusLogs",
                table: "ChannelOperationRequests",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(List<ChannelStatusLog>),
                oldType: "jsonb",
                oldNullable: true);
        }
    }
}
