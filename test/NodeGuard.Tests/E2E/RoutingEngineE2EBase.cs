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

using Microsoft.EntityFrameworkCore;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using Xunit.Abstractions;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// Shared plumbing for the routing-engine e2e tests — the fee engine and the auto-rebalancer both act on
/// <see cref="ChannelRoutingState"/>, and neither has a gRPC read path for it, so they assert against a
/// direct connection to NodeGuard's Postgres. Sits between <see cref="E2ETestBase"/> and the scenarios so
/// the non-routing e2e tests don't inherit any of it.
/// </summary>
public abstract class RoutingEngineE2EBase : E2ETestBase
{
    protected RoutingEngineE2EBase(ITestOutputHelper output) : base(output)
    {
    }

    protected static ApplicationDbContext CreateDbContext()
    {
        var cs = Env("POSTGRES_CONNECTIONSTRING", "Host=localhost;Port=25432;Database=nodeguard;User ID=postgres;");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(3), null))
            .Options;
        return new ApplicationDbContext(options);
    }

    // Truncates the engine's DERIVED state (forwarding events + routing/fee state) so a scenario starts
    // clean; Channels/Nodes are left intact so channel discovery still holds. Routing state re-seeds
    // EmaLocalRatio with the first observation after a reset, so a scenario that shapes balances first
    // gets a signal that reflects them immediately instead of an EMA still crawling out of history.
    protected static async Task ResetRoutingEngineStateAsync()
    {
        await using var db = CreateDbContext();
        await db.ForwardingHtlcEvents.ExecuteDeleteAsync();
        await db.ChannelRoutingStates.ExecuteDeleteAsync();
        await db.ChannelFeeStates.ExecuteDeleteAsync();
    }

    /// <summary>
    /// Reads one side's routing state. Both routing and fee state are keyed by (channel, managed node).
    /// </summary>
    protected static async Task<ChannelRoutingState?> ReadRoutingStateAsync(int channelId, string managedNodePubKey)
    {
        await using var db = CreateDbContext();
        return await db.ChannelRoutingStates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChannelId == channelId && x.ManagedNodePubKey == managedNodePubKey);
    }

    /// <summary>
    /// NodeGuard's channel id for an LND scid, once it has been recorded (MonitorChannelsJob picks up
    /// externally-opened channels). Returns via poll because discovery is asynchronous.
    /// </summary>
    protected Task<int> PollChannelIdByScidAsync(ulong scid, int attempts = 60)
        => PollAsync(
            async () =>
            {
                await using var db = CreateDbContext();
                return await db.Channels.AsNoTracking()
                    .Where(c => c.ChanId == scid && c.Status == Channel.ChannelStatus.Open)
                    .Select(c => c.Id)
                    .FirstOrDefaultAsync();
            },
            id => id != 0,
            attempts, TimeSpan.FromSeconds(3), $"channel with scid {scid} discovered by NodeGuard");

    /// <summary>Polls the routing state until <paramref name="done"/> holds, logging the evolving signal each attempt.</summary>
    protected Task<ChannelRoutingState?> PollRoutingStateAsync(
        int channelId, string managedNodePubKey, Func<ChannelRoutingState?, bool> done, string tag,
        string what, int attempts)
        => PollAsync(
            async () =>
            {
                var rs = await ReadRoutingStateAsync(channelId, managedNodePubKey);
                if (rs != null)
                    _output.WriteLine($"[{tag}] cat={rs.PeerFlowCategory} ema={rs.EmaLocalRatio:0.###} target={rs.TargetLocalRatio:0.###} netFlow={rs.NetFlowRatio:0.###} push={rs.PushMsatWindow} pull={rs.PullMsatWindow} age={rs.AgeBlocks} chanIdLnd={rs.ChanIdLnd}");
                return rs;
            },
            done, attempts, TimeSpan.FromSeconds(4), what);

    /// <summary>One side's fee state — keyed the same way as routing state, see <see cref="ReadRoutingStateAsync"/>.</summary>
    protected static async Task<ChannelFeeState?> ReadFeeStateAsync(int channelId, string managedNodePubKey)
    {
        await using var db = CreateDbContext();
        return await db.ChannelFeeStates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChannelId == channelId && x.ManagedNodePubKey == managedNodePubKey);
    }

    // Polls until a real fee is applied (LastAppliedOutboundPpm is never set by a NoOp).
    protected Task<ChannelFeeState?> PollFeeAppliedAsync(
        int channelId, string managedNodePubKey, string what, int attempts = 40, int delaySeconds = 4)
        => PollAsync(
            () => ReadFeeStateAsync(channelId, managedNodePubKey),
            fs => fs is { LastAppliedOutboundPpm: not null },
            attempts, TimeSpan.FromSeconds(delaySeconds), what);
}
