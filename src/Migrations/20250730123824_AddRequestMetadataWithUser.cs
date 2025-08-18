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
