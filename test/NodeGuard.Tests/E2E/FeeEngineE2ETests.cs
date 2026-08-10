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
using Xunit.Abstractions;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// E2E: the fee engine applies a real policy to an imbalanced channel, then STOPS once the channel is
/// disabled. Reuses the setup's Bob→Carol (bob-owned, imbalanced) — NodeGuard rejects a duplicate open.
/// Direction isn't asserted (covered by <see cref="FeeEngineFlowE2ETests"/>); the value here is the
/// APPLY-then-STOP-on-disable lifecycle. (Purge is UI-only, unreachable here — see FeeEngineStateServiceTests.)
/// Order-agnostic: reuses the always-present setup channel and resets its own fee-engine state.
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class FeeEngineE2ETests : FeeEngineE2EBase
{
    public FeeEngineE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact]
    public async Task FeeEngine_AppliesFeeToImbalancedChannel_ThenStopsWhenDisabled()
    {
        var client = CreateClient(out var headers);

        var nodes = await WaitForNodesAsync(client, headers);
        var bob = nodes.Single(n => n.Name == "bob");
        _output.WriteLine($"bob={bob.PubKey}");

        try
        {
            // Clean slate so leftover HTLCs from another scenario can't skew categorization.
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
}
