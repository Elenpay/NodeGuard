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
/// Per-channel fee-engine state (1:1 with <see cref="Channel"/>). Declared in the Phase 1
/// migration so Phase 2 needs no schema change; rows are not created or populated until the
/// Phase 2 fee engine ships. Holds last-applied policy + baseline snapshot (for rollback on
/// disable) + a per-channel circuit-breaker counter, all of which must survive restarts.
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

    public DateTimeOffset? BaselineCapturedAt { get; set; }
    public long? BaselineOutboundBaseFeeMsat { get; set; }
    public uint? BaselineOutboundPpm { get; set; }
    public int? BaselineInboundBaseMsat { get; set; }
    public int? BaselineInboundPpm { get; set; }

    /// <summary>
    /// Circuit-breaker counter: incremented on each consecutive failure to apply a fee update,
    /// reset to 0 on success.
    /// </summary>
    public int ConsecutiveFailures { get; set; } = 0;
}
