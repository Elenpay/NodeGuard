using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentRouteHopAttemptOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptStatus",
                table: "PaymentRouteHops",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "PaymentRouteHops",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureSourceIndex",
                table: "PaymentRouteHops",
                type: "integer",
                nullable: true);

            // Existing rows stored LND's node-global HTLCAttempt.attempt_id in AttemptIndex;
            // the tracker now stores the attempt's ordinal within its own payment. Without
            // this backfill the UI renders legacy attempts as "attempt 4021" (the trace label
            // is attemptIndex + 1). Dense-rank preserves the original attempt ordering.
            //
            // Not reversed in Down(): the original attempt_id values are not recoverable, and
            // nothing reads them — AttemptIndex only ever identifies and orders attempts
            // within one payment.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id",
                           DENSE_RANK() OVER (PARTITION BY "PaymentHash" ORDER BY "AttemptIndex") - 1 AS ordinal
                    FROM "PaymentRouteHops"
                )
                UPDATE "PaymentRouteHops" AS h
                SET "AttemptIndex" = ranked.ordinal
                FROM ranked
                WHERE h."Id" = ranked."Id" AND h."AttemptIndex" <> ranked.ordinal;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptStatus",
                table: "PaymentRouteHops");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "PaymentRouteHops");

            migrationBuilder.DropColumn(
                name: "FailureSourceIndex",
                table: "PaymentRouteHops");
        }
    }
}
