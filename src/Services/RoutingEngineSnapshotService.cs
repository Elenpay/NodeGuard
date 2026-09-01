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

using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Services;

/// <summary>
/// One of a node's channels with everything the routing-engine jobs act on, assembled from a
/// single <c>ListChannels</c> plus the per-(channel, managed node) routing and fee state.
/// </summary>
public sealed class OwnedChannel
{
    public required Lnrpc.Channel Lnd { get; init; }
    public required Channel DbChannel { get; init; }
    public required ChannelRoutingState RoutingState { get; init; }
    public required ChannelFeeState? FeeState { get; init; }
}

/// <summary>
/// Builds the per-node channel view both routing-engine actuator jobs
/// (<see cref="Jobs.ChannelFeeOptimizerJob"/> and <see cref="Jobs.AutoRebalanceJob"/>) start from.
/// The two jobs run on independent cadences, so each takes its own snapshot.
/// </summary>
public interface IRoutingEngineSnapshotService
{
    /// <summary>
    /// The node's actuatable channels: open, known to NodeGuard, and carrying a routing-state
    /// signal for this managed node. Returns null when LND is unreachable — callers should treat
    /// that as "skip this node this cycle" rather than as an empty result. Per-job eligibility
    /// (min size / IsDynamicFeeEnabled for fees, opt-in / trigger for rebalancing) is the caller's.
    /// </summary>
    Task<IReadOnlyList<OwnedChannel>?> GetOwnedChannelsAsync(
        Node node,
        IReadOnlyDictionary<ulong, Channel> openChannelsByChanId,
        bool withFeeState);
}

public class RoutingEngineSnapshotService : IRoutingEngineSnapshotService
{
    private readonly IChannelRoutingStateRepository _routingStateRepository;
    private readonly IChannelFeeStateRepository _feeStateRepository;
    private readonly ILightningClientService _lightningClientService;

    public RoutingEngineSnapshotService(
        IChannelRoutingStateRepository routingStateRepository,
        IChannelFeeStateRepository feeStateRepository,
        ILightningClientService lightningClientService)
    {
        _routingStateRepository = routingStateRepository;
        _feeStateRepository = feeStateRepository;
        _lightningClientService = lightningClientService;
    }

    public async Task<IReadOnlyList<OwnedChannel>?> GetOwnedChannelsAsync(
        Node node,
        IReadOnlyDictionary<ulong, Channel> openChannelsByChanId,
        bool withFeeState)
    {
        // One LND round-trip per node per job run.
        var listResp = await _lightningClientService.ListChannels(node);
        if (listResp == null) return null;

        var routingStates = (await _routingStateRepository.GetByManagedNodePubKey(node.PubKey))
            .ToDictionary(s => s.ChannelId);

        // Only the fee job reads these; the rebalancer never does.
        var feeStates = withFeeState
            ? (await _feeStateRepository.GetByManagedNodePubKey(node.PubKey)).ToDictionary(s => s.ChannelId)
            : new Dictionary<int, ChannelFeeState>();

        var owned = new List<OwnedChannel>();
        foreach (var lndChannel in listResp.Channels)
        {
            // No ownership dedup: routing/fee state is per (channel, managed node), so when both
            // ends are managed each side actuates its own view — its own local balance for the
            // rebalancer, its own outbound policy for the fee job.
            if (!openChannelsByChanId.TryGetValue(lndChannel.ChanId, out var dbChannel)) continue;
            if (!routingStates.TryGetValue(dbChannel.Id, out var routingState)) continue; // no signal yet

            feeStates.TryGetValue(dbChannel.Id, out var feeState);
            owned.Add(new OwnedChannel
            {
                Lnd = lndChannel,
                DbChannel = dbChannel,
                RoutingState = routingState,
                FeeState = feeState,
            });
        }

        return owned;
    }
}
