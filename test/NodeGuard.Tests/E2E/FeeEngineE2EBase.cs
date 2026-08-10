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

namespace NodeGuard.Tests.E2E;

/// <summary>
/// Base for the fee-engine e2e tests: a direct connection to NodeGuard's Postgres plus the fee-engine state
/// helpers they assert on (there is no gRPC read path for ChannelRoutingState/ChannelFeeState). Sits between
/// <see cref="E2ETestBase"/> and the fee tests so non-fee e2e tests don't inherit fee-specific plumbing.
/// </summary>
public abstract class FeeEngineE2EBase : E2ETestBase
{
    protected FeeEngineE2EBase(ITestOutputHelper output) : base(output)
    {
    }

    protected static ApplicationDbContext CreateDbContext()
    {
        var cs = Env("NODEGUARD_DB_CONNECTIONSTRING", "Host=localhost;Port=25432;Database=nodeguard;User ID=postgres;");
        // Retry transient failures — a momentary DNS/socket blip must not fail a multi-minute run.
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(3), null))
            .Options;
        return new ApplicationDbContext(options);
    }

    // Truncates the engine's DERIVED state (forwarding events + routing/fee state) so a scenario starts
    // clean; Channels/Nodes are left intact so channel discovery still holds.
    protected static async Task ResetFeeEngineStateAsync()
    {
        await using var db = CreateDbContext();
        await db.ForwardingHtlcEvents.ExecuteDeleteAsync();
        await db.ChannelRoutingStates.ExecuteDeleteAsync();
        await db.ChannelFeeStates.ExecuteDeleteAsync();
    }

    protected static async Task<ChannelFeeState?> ReadFeeStateAsync(int channelId)
    {
        await using var db = CreateDbContext();
        return await db.ChannelFeeStates.AsNoTracking().FirstOrDefaultAsync(x => x.ChannelId == channelId);
    }

    // Polls until a real fee is applied (LastAppliedOutboundPpm is never set by a NoOp).
    protected Task<ChannelFeeState?> PollFeeAppliedAsync(int channelId, string what, int attempts = 40, int delaySeconds = 4)
        => PollAsync(
            () => ReadFeeStateAsync(channelId),
            fs => fs is { LastAppliedOutboundPpm: not null },
            attempts, TimeSpan.FromSeconds(delaySeconds), what);
}
