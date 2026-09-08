using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <summary>
    /// Enforces at most one template PSBT per withdrawal request and per channel operation request.
    /// <para>
    /// The template is the transaction every approval is validated against. Two concurrent
    /// GenerateTemplatePSBT calls (a double click on Approve) used to persist two templates with different
    /// txids, so an approver signed one while the validator compared against the other. Partial unique
    /// indexes make a second template row impossible.
    /// </para>
    /// <para>
    /// Existing duplicates are repaired here, in SQL, right before the indexes are created. For a request
    /// that went through (it has a TxId) the template whose txid equals that TxId survives; the txid is
    /// computed in SQL from the PSBT's unsigned transaction. For every other request (cancelled, rejected,
    /// failed, or without a TxId) the oldest row survives. Removed rows are copied to
    /// "&lt;Table&gt;_RemovedDuplicateTemplates", outside the EF model. A request that was still collecting
    /// signatures is marked Failed, since the template its approvers were shown may be the one removed.
    /// </para>
    /// </summary>
    public partial class EnforceSingleTemplatePsbtPerRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WalletWithdrawalRequestStatus: Pending = 0, PSBTSignaturesPending = 3, FinalizingPSBT = 7, Failed = 6
            migrationBuilder.Sql(Dedupe("WalletWithdrawalRequestPSBTs", "WalletWithdrawalRequestId", "WalletWithdrawalRequests",
                activeStatuses: "0, 3, 7", failedStatus: 6));

            // ChannelOperationRequestStatus: Pending = 4, PSBTSignaturesPending = 5, FinalizingPSBT = 9, Failed = 8
            migrationBuilder.Sql(Dedupe("ChannelOperationRequestPSBTs", "ChannelOperationRequestId", "ChannelOperationRequests",
                activeStatuses: "4, 5, 9", failedStatus: 8));

            migrationBuilder.CreateIndex(
                name: "IX_WalletWithdrawalRequestPSBTs_Template",
                table: "WalletWithdrawalRequestPSBTs",
                column: "WalletWithdrawalRequestId",
                unique: true,
                filter: "\"IsTemplatePSBT\"");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequestPSBTs_Template",
                table: "ChannelOperationRequestPSBTs",
                column: "ChannelOperationRequestId",
                unique: true,
                filter: "\"IsTemplatePSBT\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletWithdrawalRequestPSBTs_Template",
                table: "WalletWithdrawalRequestPSBTs");

            migrationBuilder.DropIndex(
                name: "IX_ChannelOperationRequestPSBTs_Template",
                table: "ChannelOperationRequestPSBTs");

            // Rows removed by Up stay in the *_RemovedDuplicateTemplates tables and are intentionally not restored.
        }

        /// <summary>
        /// Removes all but one template PSBT per request. Survivor: the template whose txid equals the request's
        /// TxId when there is one; otherwise the oldest row. The txid is the byte-reversed double SHA-256 of the
        /// PSBT's unsigned transaction, which in a NodeGuard-built PSBT is the first global map entry: 5-byte
        /// magic, key length 0x01, key type 0x00, compact-size value length, transaction bytes. A PSBT that does
        /// not have that shape yields a NULL txid and simply never wins on the txid rule.
        /// </summary>
        private static string Dedupe(string psbtTable, string requestIdColumn, string requestTable,
            string activeStatuses, int failedStatus) => $@"
CREATE TABLE IF NOT EXISTS ""{psbtTable}_RemovedDuplicateTemplates"" AS
SELECT p.*, now() AS ""RemovedAt"" FROM ""{psbtTable}"" p WHERE false;

CREATE TEMP TABLE dup_templates ON COMMIT DROP AS
WITH t AS (
    SELECT p.""Id"", p.""{requestIdColumn}"" AS req, r.""TxId"", decode(p.""PSBT"", 'base64') AS b
    FROM ""{psbtTable}"" p
    JOIN ""{requestTable}"" r ON r.""Id"" = p.""{requestIdColumn}""
    WHERE p.""IsTemplatePSBT""
      AND p.""{requestIdColumn}"" IN (
          SELECT ""{requestIdColumn}"" FROM ""{psbtTable}"" WHERE ""IsTemplatePSBT"" GROUP BY 1 HAVING count(*) > 1)
), tx AS (
    SELECT ""Id"", req, ""TxId"",
        CASE WHEN octet_length(b) < 12
                  OR substring(b from 1 for 5) <> '\x70736274ff'::bytea
                  OR get_byte(b, 5) <> 1 OR get_byte(b, 6) <> 0 THEN NULL
             WHEN get_byte(b, 7) < 253 THEN substring(b from 9 for get_byte(b, 7))
             WHEN get_byte(b, 7) = 253 THEN substring(b from 11 for get_byte(b, 8) + 256 * get_byte(b, 9))
             WHEN get_byte(b, 7) = 254 THEN substring(b from 13 for get_byte(b, 8) + 256 * get_byte(b, 9)
                                                              + 65536 * get_byte(b, 10) + 16777216 * get_byte(b, 11))
        END AS unsigned_tx
    FROM t
), h AS (
    SELECT ""Id"", req, ""TxId"", encode(sha256(sha256(unsigned_tx)), 'hex') AS le FROM tx
), ranked AS (
    SELECT ""Id"", req,
        row_number() OVER (
            PARTITION BY req
            ORDER BY ((SELECT string_agg(substr(le, 65 - 2 * i, 2), '') FROM generate_series(1, 32) i) = ""TxId"") DESC NULLS LAST,
                     ""Id"") AS rn
    FROM h
)
SELECT ""Id"", req FROM ranked WHERE rn > 1;

INSERT INTO ""{psbtTable}_RemovedDuplicateTemplates""
SELECT p.*, now() FROM ""{psbtTable}"" p WHERE p.""Id"" IN (SELECT ""Id"" FROM dup_templates);

DELETE FROM ""{psbtTable}"" WHERE ""Id"" IN (SELECT ""Id"" FROM dup_templates);

UPDATE ""{requestTable}"" SET ""Status"" = {failedStatus}, ""UpdateDatetime"" = now()
WHERE ""Id"" IN (SELECT DISTINCT req FROM dup_templates) AND ""Status"" IN ({activeStatuses});

DROP TABLE dup_templates;";
    }
}
