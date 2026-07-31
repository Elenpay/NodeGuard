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

namespace NodeGuard.Services;

/// <summary>
/// Owns the lifecycle of per-channel fee-engine state (<see cref="ChannelFeeState"/>) around
/// enable/disable transitions. When a channel opts out of the engine, or its node is disabled or
/// has fee management switched off, the stale control-loop state is dropped so a future re-enable
/// cold-starts from the category baseline instead of resuming from an outdated operating point.
/// </summary>
public interface IFeeEngineStateService
{
    /// <summary>
    /// Removes the channel's fee state when it is no longer opted in to the fee engine
    /// (<see cref="Channel.IsDynamicFeeEnabled"/> == false). No-op while it stays enabled.
    /// </summary>
    Task PurgeChannelStateIfDisabled(Channel channel);

    /// <summary>
    /// Removes the fee state for every channel on the node when the node no longer manages fees —
    /// i.e. it is disabled (<see cref="Node.IsNodeDisabled"/>) or has the master fee gate off
    /// (<see cref="Node.DynamicFeeManagementEnabled"/> == false). No-op while the node manages fees.
    /// </summary>
    Task PurgeNodeStateIfDisabled(Node node);
}

public class FeeEngineStateService : IFeeEngineStateService
{
    private readonly IChannelFeeStateRepository _feeStateRepository;
    private readonly ILogger<FeeEngineStateService> _logger;

    public FeeEngineStateService(
        IChannelFeeStateRepository feeStateRepository,
        ILogger<FeeEngineStateService> logger)
    {
        _feeStateRepository = feeStateRepository;
        _logger = logger;
    }

    public async Task PurgeChannelStateIfDisabled(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.IsDynamicFeeEnabled)
        {
            return;
        }

        var deleted = await _feeStateRepository.DeleteByChannelId(channel.Id);
        if (deleted)
        {
            _logger.LogInformation(
                "Purged fee state for channel {ChannelId} (ChanId {ChanId}) — dynamic fee management disabled for the channel",
                channel.Id, channel.ChanId);
        }
    }

    public async Task PurgeNodeStateIfDisabled(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsNodeDisabled && node.DynamicFeeManagementEnabled)
        {
            return;
        }

        var deleted = await _feeStateRepository.DeleteByManagedNodePubKey(node.PubKey);
        if (deleted)
        {
            _logger.LogInformation(
                "Purged fee state for node {NodeName} ({PubKey}) — fee management disabled for the node",
                node.Name, node.PubKey);
        }
    }
}
