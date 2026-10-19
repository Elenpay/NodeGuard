using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <summary>
    /// Bumped becomes terminal: a withdrawal request's status describes only its own transaction.
    /// <para>
    /// Until now a request was marked Bumped (or Cancelled with a "Cancelled by RBF replacement request N" reason) as
    /// soon as its RBF replacement was executed, and reverted if the replacement was abandoned. From now on nothing
    /// writes the replaced request while the replacement is in flight: the API derives what it reports from the
    /// replacement, and the monitor stores Bumped / Cancelled only once a replacement confirms, or settles the
    /// request itself when it was mined instead.
    /// </para>
    /// <para>
    /// Existing rows are repaired here, in SQL. A Bumped or RBF-cancelled request none of whose replacements (direct
    /// or through further replacements) confirmed goes back to OnChainConfirmationPending: its own transaction is
    /// still the live candidate, so the monitor polls it again and the UI offers to replace it again. Rows whose
    /// replacement confirmed are already in their final status and are left alone.
    /// </para>
    /// <para>
    /// Then a partial unique index guarantees at most one live replacement per request. The migration raises a
    /// readable error when existing data violates that, instead of a bare unique-constraint failure.
    /// </para>
    /// </summary>
    public partial class BumpedIsTerminalOnly : Migration
    {
        // WalletWithdrawalRequestStatus: Pending = 0, Cancelled = 1, PSBTSignaturesPending = 3,
        // OnChainConfirmationPending = 4, OnChainConfirmed = 5, FinalizingPSBT = 7, Bumped = 8
        private const string Repair = @"
WITH RECURSIVE roots AS (
    SELECT ""Id"" FROM ""WalletWithdrawalRequests""
    WHERE ""Status"" = 8
       OR (""Status"" = 1 AND ""RejectCancelDescription"" LIKE 'Cancelled by RBF replacement request%')
), descendants AS (
    SELECT r.""Id"" AS root_id, r.""Id"" AS node_id FROM roots r
  UNION
    SELECT d.root_id, c.""Id"" FROM descendants d
    JOIN ""WalletWithdrawalRequests"" c ON c.""BumpingWalletWithdrawalRequestId"" = d.node_id
), settled AS (
    SELECT DISTINCT d.root_id FROM descendants d
    JOIN ""WalletWithdrawalRequests"" n ON n.""Id"" = d.node_id
    WHERE n.""Id"" <> d.root_id AND n.""Status"" = 5
)
UPDATE ""WalletWithdrawalRequests"" w
SET ""Status"" = 4,
    ""RejectCancelDescription"" = CASE WHEN w.""Status"" = 1 THEN NULL ELSE w.""RejectCancelDescription"" END,
    ""UpdateDatetime"" = now()
WHERE w.""Id"" IN (SELECT ""Id"" FROM roots) AND w.""Id"" NOT IN (SELECT root_id FROM settled);";

        private const string Guard = @"
DO $$
DECLARE v text;
BEGIN
  SELECT string_agg(format('%s -> {%s}', orig, reps), '; ') INTO v FROM (
    SELECT ""BumpingWalletWithdrawalRequestId"" AS orig, string_agg(""Id""::text, ',' ORDER BY ""Id"") AS reps
    FROM ""WalletWithdrawalRequests""
    WHERE ""BumpingWalletWithdrawalRequestId"" IS NOT NULL AND ""Status"" IN (0, 3, 4, 7)
    GROUP BY 1 HAVING count(*) > 1) x;
  IF v IS NOT NULL THEN
    RAISE EXCEPTION 'Withdrawal requests with more than one live RBF replacement, resolve them by hand before migrating: %', v;
  END IF;
END $$;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Repair);
            migrationBuilder.Sql(Guard);

            migrationBuilder.CreateIndex(
                name: "IX_WalletWithdrawalRequests_LiveReplacement",
                table: "WalletWithdrawalRequests",
                column: "BumpingWalletWithdrawalRequestId",
                unique: true,
                filter: "\"BumpingWalletWithdrawalRequestId\" IS NOT NULL AND \"Status\" IN (0, 3, 4, 7)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The data repair is not reversed: the revived rows are pending confirmation, which is what they are.
            migrationBuilder.DropIndex(
                name: "IX_WalletWithdrawalRequests_LiveReplacement",
                table: "WalletWithdrawalRequests");
        }
    }
}
