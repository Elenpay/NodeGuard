using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddSparkSignerIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SparkSignerIdentities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SeedFingerprint = table.Column<string>(type: "text", nullable: false),
                    Account = table.Column<int>(type: "integer", nullable: false),
                    IdentityPublicKey = table.Column<string>(type: "text", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SparkSignerIdentities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SparkSignerIdentities_SeedFingerprint_Account",
                table: "SparkSignerIdentities",
                columns: new[] { "SeedFingerprint", "Account" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SparkSignerIdentities");
        }
    }
}
