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

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IChannelOpenRecommendationRepository
{
    Task<ChannelOpenRecommendation?> GetById(int id);

    Task<List<ChannelOpenRecommendation>> GetOpenByNode(int nodeId);

    /// <summary>
    /// A promotion whose channel open is still unresolved is left untouched; every other row is
    /// recycled into a fresh Open one. The returned row's status tells the caller which case it hit.
    /// </summary>
    Task<(ChannelOpenRecommendation? Persisted, string? Error)> Upsert(ChannelOpenRecommendation recommendation);

    Task<(bool, string?)> Update(ChannelOpenRecommendation recommendation);

    /// <summary>
    /// Runs first each cycle; the upsert re-opens what still qualifies, so the panel is always the
    /// latest run's output.
    /// </summary>
    Task<int> ExpireOpen(int nodeId);

    /// <summary>
    /// Feeds the per-peer cooldown, which is what stops a drained sink peer being re-proposed every run.
    /// </summary>
    Task<Dictionary<string, DateTimeOffset>> GetLastDecisionByPeer(int nodeId);
}
