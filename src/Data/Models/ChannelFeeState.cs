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

namespace NodeGuard.Data.Models;

/// <summary>
/// Per-channel fee-engine state (1:1 with <see cref="Channel"/>). Holds last-applied
/// policy and control state that must survive restarts.
/// </summary>
public class ChannelFeeState : Entity
{
    /// <summary>FK to <see cref="Channel"/> (unique — one fee state per channel).</summary>
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;

    public DateTimeOffset? LastFeeUpdateAt { get; set; }
    public long? LastAppliedOutboundBaseFeeMsat { get; set; }
    public uint? LastAppliedOutboundPpm { get; set; }
    public int? LastAppliedInboundBaseMsat { get; set; }
    public int? LastAppliedInboundPpm { get; set; }
    public double? LastComputedTarget { get; set; }
    public double? LastObservedRatio { get; set; }
}
