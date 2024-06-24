using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class UtxoFreezing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "FMUTXOs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFrozen",
                table: "FMUTXOs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TagId",
                table: "FMUTXOs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalletId",
                table: "FMUTXOs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "UTXOTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UTXOTags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FMUTXOs_TagId",
                table: "FMUTXOs",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_FMUTXOs_WalletId",
                table: "FMUTXOs",
                column: "WalletId");

            migrationBuilder.AddForeignKey(
                name: "FK_FMUTXOs_UTXOTags_TagId",
                table: "FMUTXOs",
                column: "TagId",
                principalTable: "UTXOTags",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_FMUTXOs_Wallets_WalletId",
                table: "FMUTXOs",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FMUTXOs_UTXOTags_TagId",
                table: "FMUTXOs");

            migrationBuilder.DropForeignKey(
                name: "FK_FMUTXOs_Wallets_WalletId",
                table: "FMUTXOs");

            migrationBuilder.DropTable(
                name: "UTXOTags");

            migrationBuilder.DropIndex(
                name: "IX_FMUTXOs_TagId",
                table: "FMUTXOs");

            migrationBuilder.DropIndex(
                name: "IX_FMUTXOs_WalletId",
                table: "FMUTXOs");

            migrationBuilder.DropColumn(
                name: "Address",
                table: "FMUTXOs");

            migrationBuilder.DropColumn(
                name: "IsFrozen",
                table: "FMUTXOs");

            migrationBuilder.DropColumn(
                name: "TagId",
                table: "FMUTXOs");

            migrationBuilder.DropColumn(
                name: "WalletId",
                table: "FMUTXOs");
        }
    }
}
