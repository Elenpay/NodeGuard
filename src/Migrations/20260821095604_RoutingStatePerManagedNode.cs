using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <summary>
    /// Re-keys the routing-engine read models from "one row per channel" to "one row per channel
    /// per managed node".
    /// <para>
    /// A channel between two managed nodes used to get a single row, assigned to the initiator
    /// side. The other side was then invisible to the routing engine: it could not see its own
    /// depleted channels, so they never classified as rebalance destinations and its outbound fee
    /// policy was never managed. Both sides now carry their own state.
    /// </para>
    /// </summary>
    public partial class RoutingStatePerManagedNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChannelRoutingStates_ChannelId",
                table: "ChannelRoutingStates");

            migrationBuilder.DropIndex(
                name: "IX_ChannelFeeStates_ChannelId",
                table: "ChannelFeeStates");

            migrationBuilder.AddColumn<string>(
                name: "ManagedNodePubKey",
                table: "ChannelFeeStates",
                type: "text",
                nullable: false,
                defaultValue: "");

            // ChannelFeeState had no owning node of its own — it was resolved by joining through
            // ChannelRoutingState, which was 1:1 with the channel. Carry that owner across so the
            // fee control loop keeps its operating point instead of cold-starting everywhere.
            migrationBuilder.Sql(@"
                UPDATE ""ChannelFeeStates"" fs
                SET ""ManagedNodePubKey"" = rs.""ManagedNodePubKey""
                FROM ""ChannelRoutingStates"" rs
                WHERE rs.""ChannelId"" = fs.""ChannelId"";");

            // Any fee state we could not attribute to a node is unreachable by the engine — drop it
            // so the channel cold-starts from its category baseline on the next cycle.
            migrationBuilder.Sql(@"DELETE FROM ""ChannelFeeStates"" WHERE ""ManagedNodePubKey"" = '';");

            // The empty-string default existed only to backfill; new rows must always name a node.
            migrationBuilder.Sql(@"ALTER TABLE ""ChannelFeeStates"" ALTER COLUMN ""ManagedNodePubKey"" DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelRoutingStates_ChannelId_ManagedNodePubKey",
                table: "ChannelRoutingStates",
                columns: new[] { "ChannelId", "ManagedNodePubKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelFeeStates_ChannelId_ManagedNodePubKey",
                table: "ChannelFeeStates",
                columns: new[] { "ChannelId", "ManagedNodePubKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChannelRoutingStates_ChannelId_ManagedNodePubKey",
                table: "ChannelRoutingStates");

            migrationBuilder.DropIndex(
                name: "IX_ChannelFeeStates_ChannelId_ManagedNodePubKey",
                table: "ChannelFeeStates");

            // Going back to one row per channel: keep the oldest side, drop the rest, or the
            // unique index below cannot be recreated.
            migrationBuilder.Sql(@"
                DELETE FROM ""ChannelRoutingStates"" a
                USING ""ChannelRoutingStates"" b
                WHERE a.""ChannelId"" = b.""ChannelId"" AND a.""Id"" > b.""Id"";");

            migrationBuilder.Sql(@"
                DELETE FROM ""ChannelFeeStates"" a
                USING ""ChannelFeeStates"" b
                WHERE a.""ChannelId"" = b.""ChannelId"" AND a.""Id"" > b.""Id"";");

            migrationBuilder.DropColumn(
                name: "ManagedNodePubKey",
                table: "ChannelFeeStates");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelRoutingStates_ChannelId",
                table: "ChannelRoutingStates",
                column: "ChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelFeeStates_ChannelId",
                table: "ChannelFeeStates",
                column: "ChannelId",
                unique: true);
        }
    }
}
