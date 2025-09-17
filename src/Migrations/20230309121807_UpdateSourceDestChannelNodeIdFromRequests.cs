// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

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
