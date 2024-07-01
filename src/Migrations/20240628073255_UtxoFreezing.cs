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
            migrationBuilder.CreateTable(
                name: "UTXOTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Outpoint = table.Column<string>(type: "text", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UTXOTags", x => x.Id);
                    table.UniqueConstraint("Unique_UTXOTags_Key_Outpoint", x => new { x.Key, x.Outpoint });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UTXOTags");
        }
    }
}
