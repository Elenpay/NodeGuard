using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class NewAttributesUTXOs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "FMUTXOs",
                type: "text",
                nullable: false,
                defaultValue: "");

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
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "UTXOTag",
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
                    table.PrimaryKey("PK_UTXOTag", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FMUTXOs_TagId",
                table: "FMUTXOs",
                column: "TagId");

            migrationBuilder.AddForeignKey(
                name: "FK_FMUTXOs_UTXOTag_TagId",
                table: "FMUTXOs",
                column: "TagId",
                principalTable: "UTXOTag",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
            
            migrationBuilder.AddUniqueConstraint(
                name: "Unique_UTXO",
                table: "FMUTXOs",
                columns: new []{"TxId", "OutputIndex"});
                
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FMUTXOs_UTXOTag_TagId",
                table: "FMUTXOs");

            migrationBuilder.DropTable(
                name: "UTXOTag");

            migrationBuilder.DropIndex(
                name: "IX_FMUTXOs_TagId",
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

            migrationBuilder.DropUniqueConstraint(
                name: "Unique_UTXO",
                table: "FMUTXOs");
        }
    }
}
