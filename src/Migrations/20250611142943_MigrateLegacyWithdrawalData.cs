using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class MigrateLegacyWithdrawalData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Execute data migration in an explicit transaction
            migrationBuilder.Sql(@"
                BEGIN;
                
                -- Step 1: Migrate existing data from Amount and DestinationAddress to WalletWithdrawalRequestDestinations
                INSERT INTO ""WalletWithdrawalRequestDestinations"" (""Amount"", ""Address"", ""WalletWithdrawalRequestId"", ""CreationDatetime"", ""UpdateDatetime"")
                SELECT 
                    ""Amount"",
                    ""DestinationAddress"",
                    ""Id"",
                    ""CreationDatetime"",
                    ""UpdateDatetime""
                FROM ""WalletWithdrawalRequests""
                WHERE ""Amount"" > 0 AND ""DestinationAddress"" IS NOT NULL AND ""DestinationAddress"" != '';
                
                -- Step 2: Verify migration was successful - if any records failed to migrate, rollback
                DO $$
                DECLARE
                    original_count INTEGER;
                    migrated_count INTEGER;
                BEGIN
                    -- Count original records that should have been migrated
                    SELECT COUNT(*) INTO original_count
                    FROM ""WalletWithdrawalRequests""
                    WHERE ""Amount"" > 0 AND ""DestinationAddress"" IS NOT NULL AND ""DestinationAddress"" != '';
                    
                    -- Count migrated records that were just inserted
                    SELECT COUNT(*) INTO migrated_count
                    FROM ""WalletWithdrawalRequestDestinations"" dest
                    INNER JOIN ""WalletWithdrawalRequests"" req ON dest.""WalletWithdrawalRequestId"" = req.""Id""
                    WHERE req.""Amount"" > 0 AND req.""DestinationAddress"" IS NOT NULL AND req.""DestinationAddress"" != '';
                    
                    -- If counts don't match, raise an exception to rollback the transaction
                    IF original_count != migrated_count THEN
                        RAISE EXCEPTION 'Data migration failed: Expected % records, but migrated % records. Transaction will be rolled back.', original_count, migrated_count;
                    END IF;
                    
                    -- Log success
                    RAISE NOTICE 'Successfully migrated % legacy withdrawal records to destinations table', original_count;
                END $$;
                
                COMMIT;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback: Delete the migrated destinations in an explicit transaction
            migrationBuilder.Sql(@"
                BEGIN;
                
                -- Delete only the migrated destinations (those that match legacy withdrawal requests)
                DELETE FROM ""WalletWithdrawalRequestDestinations""
                WHERE ""WalletWithdrawalRequestId"" IN (
                    SELECT ""Id"" FROM ""WalletWithdrawalRequests""
                    WHERE ""Amount"" > 0 AND ""DestinationAddress"" IS NOT NULL AND ""DestinationAddress"" != ''
                )
                AND ""Address"" IN (
                    SELECT ""DestinationAddress"" FROM ""WalletWithdrawalRequests""
                    WHERE ""Amount"" > 0 AND ""DestinationAddress"" IS NOT NULL AND ""DestinationAddress"" != ''
                )
                AND ""Amount"" IN (
                    SELECT ""Amount"" FROM ""WalletWithdrawalRequests""
                    WHERE ""Amount"" > 0 AND ""DestinationAddress"" IS NOT NULL AND ""DestinationAddress"" != ''
                );
                
                COMMIT;
            ");
        }
    }
}
