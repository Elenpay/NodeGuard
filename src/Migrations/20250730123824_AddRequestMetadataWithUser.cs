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
    /// <inheritdoc />
    public partial class AddRequestMetadataWithUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Update existing WalletWithdrawalRequests to populate RequestMetadata with username
            migrationBuilder.Sql(@"
                UPDATE ""WalletWithdrawalRequests"" 
                SET ""RequestMetadata"" = 
                    CASE 
                        WHEN ""RequestMetadata"" IS NULL OR ""RequestMetadata"" = '' THEN 
                            '{""userName"":""' || COALESCE(u.""UserName"", '') || '""}' 
                        ELSE 
                            CASE 
                                WHEN ""RequestMetadata""::jsonb ? 'userName' THEN 
                                    ""RequestMetadata""
                                ELSE 
                                    jsonb_set(""RequestMetadata""::jsonb, '{userName}', to_jsonb(COALESCE(u.""UserName"", '')))::text
                            END
                    END
                FROM ""AspNetUsers"" u 
                WHERE ""WalletWithdrawalRequests"".""UserRequestorId"" = u.""Id""
                    AND ""WalletWithdrawalRequests"".""UserRequestorId"" IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This migration does not require a down method as it is a no-op
        }
    }
}
