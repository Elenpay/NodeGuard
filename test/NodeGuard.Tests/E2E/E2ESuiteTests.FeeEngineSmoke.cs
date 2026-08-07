/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Tests.E2E;

// Part of E2ESuiteTests (overview in E2ESuiteTests.Rebalance.cs): scenario (2), plus the fee-engine DB
// helpers shared with (3) — the fee scenarios read ChannelRoutingState/ChannelFeeState directly (no gRPC
// read path exists for them).
public partial class E2ESuiteTests
{
    // (2) Fee engine applies a real policy to an imbalanced channel, then stops once disabled. Reuses the
    // setup's Bob→Carol (bob-owned, imbalanced) — NodeGuard rejects a duplicate open. Direction isn't
    // asserted (covered by (3)); the value here is the APPLY-then-STOP-on-disable lifecycle. (Purge is
    // UI-only, unreachable here — see FeeEngineStateServiceTests.)
    [E2EFact, TestPriority(2)]
    public async Task FeeEngine_AppliesFeeToImbalancedChannel_ThenStopsWhenDisabled()
    {
        var client = CreateClient(out var headers);

        var nodes = await WaitForNodesAsync(client, headers);
        var bob = nodes.Single(n => n.Name == "bob");
        _output.WriteLine($"bob={bob.PubKey}");

        try
        {
            // Clean slate so leftover HTLCs from (1) can't skew categorization.
            await ResetFeeEngineStateAsync();

            // Enable bob's fee engine. ExecuteUpdate avoids materialising the Node's encrypted macaroon column.
            await using (var db = CreateDbContext())
            {
                var updated = await db.Nodes
                    .Where(n => n.PubKey == bob.PubKey)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(n => n.DynamicFeeManagementEnabled, true)
                        .SetProperty(n => n.RoutingEngineDryRun, false));
                updated.Should().Be(1, "bob should be seeded and have its fee engine enabled");
            }

            int bobNodeId;
            await using (var db = CreateDbContext())
            {
                bobNodeId = await db.Nodes.Where(n => n.PubKey == bob.PubKey).Select(n => n.Id).FirstAsync();
            }

            // bob initiated Bob→Carol, so NodeGuard records it as SourceNodeId. Poll — it's discovered by
            // ChannelMonitorJob's first scan at startup.
            var channelId = await PollAsync(
                async () =>
                {
                    await using var db = CreateDbContext();
                    return await db.Channels.AsNoTracking()
                        .Where(c => c.SourceNodeId == bobNodeId && c.Status == Channel.ChannelStatus.Open && c.ChanId != 0)
                        .Select(c => c.Id)
                        .FirstOrDefaultAsync();
                },
                id => id != 0,
                attempts: 40, delay: TimeSpan.FromSeconds(3), what: "bob's Bob→Carol channel discovered");
            _output.WriteLine($"bob channel id={channelId}");

            // Opt in; the row-count assert catches a silent 0-row update ("engine never acts" trap).
            await using (var db = CreateDbContext())
            {
                var optedIn = await db.Channels
                    .Where(c => c.Id == channelId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDynamicFeeEnabled, true));
                optedIn.Should().Be(1, "the channel row must exist and be opted in to the fee engine");
            }

            // LastAppliedOutboundPpm is set only on a real Update (a NoOp leaves it null), so this waits for
            // an actual fee write.
            var feeState = await PollFeeAppliedAsync(channelId, "ChannelFeeState fee applied", delaySeconds: 3);
            _output.WriteLine($"feeState: outbound={feeState!.LastAppliedOutboundPpm} inbound={feeState.LastAppliedInboundPpm} at={feeState.LastFeeUpdateAt:o}");
            feeState.LastAppliedOutboundPpm.Should().NotBeNull();
            feeState.LastFeeUpdateAt.Should().NotBeNull();

            // Disable it; the engine should stop touching it.
            await using (var db = CreateDbContext())
            {
                await db.Channels
                    .Where(c => c.Id == channelId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDynamicFeeEnabled, false));
            }

            // Snapshot after disable, then again past several optimizer cycles: comparing two post-disable
            // reads is race-free vs the last pre-disable update. (LastFeeUpdateAt is written only by
            // ChannelFeeOptimizerJob.)
            await Task.Delay(TimeSpan.FromSeconds(6));
            var afterDisable = await ReadFeeStateAsync(channelId);
            afterDisable.Should().NotBeNull();

            await Task.Delay(TimeSpan.FromSeconds(18));
            var settled = await ReadFeeStateAsync(channelId);
            settled.Should().NotBeNull();

            settled!.LastFeeUpdateAt.Should().Be(afterDisable!.LastFeeUpdateAt,
                "the engine must not update a channel that has opted out");
            settled.LastAppliedOutboundPpm.Should().Be(afterDisable.LastAppliedOutboundPpm);
        }
        finally
        {
            // Leave bob un-managed for the next run.
            await using var db = CreateDbContext();
            await db.Nodes
                .Where(n => n.PubKey == bob.PubKey)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.DynamicFeeManagementEnabled, false));
        }
    }

    // ---- fee-engine DB helpers (shared by scenarios (2) and (3)) ----

    // Truncates the engine's DERIVED state (forwarding events + routing/fee state) so a scenario starts
    // clean; Channels/Nodes are left intact so channel discovery still holds.
    private static async Task ResetFeeEngineStateAsync()
    {
        await using var db = CreateDbContext();
        await db.ForwardingHtlcEvents.ExecuteDeleteAsync();
        await db.ChannelRoutingStates.ExecuteDeleteAsync();
        await db.ChannelFeeStates.ExecuteDeleteAsync();
    }

    private static async Task<ChannelFeeState?> ReadFeeStateAsync(int channelId)
    {
        await using var db = CreateDbContext();
        return await db.ChannelFeeStates.AsNoTracking().FirstOrDefaultAsync(x => x.ChannelId == channelId);
    }

    // Polls until a real fee is applied (LastAppliedOutboundPpm is never set by a NoOp).
    private Task<ChannelFeeState?> PollFeeAppliedAsync(int channelId, string what, int attempts = 40, int delaySeconds = 4)
        => PollAsync(
            () => ReadFeeStateAsync(channelId),
            fs => fs is { LastAppliedOutboundPpm: not null },
            attempts, TimeSpan.FromSeconds(delaySeconds), what);

    private static ApplicationDbContext CreateDbContext()
    {
        var cs = Env("NODEGUARD_DB_CONNECTIONSTRING", "Host=localhost;Port=25432;Database=nodeguard;User ID=postgres;");
        // Retry transient failures — a momentary DNS/socket blip must not fail a multi-minute run.
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(3), null))
            .Options;
        return new ApplicationDbContext(options);
    }
}
