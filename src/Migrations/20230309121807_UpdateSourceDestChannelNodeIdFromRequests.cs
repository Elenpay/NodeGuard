using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    public partial class UpdateSourceDestChannelNodeIdFromRequests : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            string query = @"
UPDATE public.""Channels"" AS c
SET
    ""SourceNodeId"" = cor.""SourceNodeId"",
    ""DestinationNodeId"" = cor.""DestNodeId""
FROM (
    SELECT
        ""ChannelId"",
        ""SourceNodeId"",
        ""DestNodeId"",
        ""RequestType""
    FROM public.""ChannelOperationRequests""
    WHERE ""SourceNodeId"" IS NOT NULL AND ""DestNodeId"" IS NOT NULL AND ""ChannelId"" IS NOT NULL AND ""RequestType"" = 1
    ORDER BY ""CreationDatetime""
) AS cor
WHERE c.""Id"" = cor.""ChannelId"";
";

            migrationBuilder.Sql(query);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            string query = @"
BEGIN TRANSACTION;
    UPDATE public.""Channels"" SET ""SourceNodeId"" = 1, ""DestinationNodeId"" = 1;
COMMIT;";
            migrationBuilder.Sql(query);

        }
    }
}
