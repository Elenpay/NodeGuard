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
using Npgsql;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories;

public class ForwardingHtlcEventRepository : IForwardingHtlcEventRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<ForwardingHtlcEventRepository> _logger;

    public ForwardingHtlcEventRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILogger<ForwardingHtlcEventRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<(bool, string?)> UpsertAsync(ForwardingHtlcEvent forwardingHtlcEvent)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await dbContext.ForwardingHtlcEvents.FindAsync(
                forwardingHtlcEvent.ManagedNodePubKey,
                forwardingHtlcEvent.IncomingChannelId,
                forwardingHtlcEvent.OutgoingChannelId,
                forwardingHtlcEvent.IncomingHtlcId,
                forwardingHtlcEvent.OutgoingHtlcId
            );

            if (existing == null)
            {
                forwardingHtlcEvent.CreationDatetime = now;
                forwardingHtlcEvent.UpdateDatetime = now;
                await dbContext.AddAsync(forwardingHtlcEvent);
            }
            else
            {
                existing.ManagedNodeName = MergeString(existing.ManagedNodeName, forwardingHtlcEvent.ManagedNodeName) ?? existing.ManagedNodeName;
                existing.EventTimestamp = forwardingHtlcEvent.EventTimestamp;
                existing.EventType = MergeEnum(existing.EventType, forwardingHtlcEvent.EventType);
                existing.EventCase = MergeEnum(existing.EventCase, forwardingHtlcEvent.EventCase);
                existing.Outcome = MergeEnum(existing.Outcome, forwardingHtlcEvent.Outcome);
                existing.IncomingTimelock = MergeNullable(existing.IncomingTimelock, forwardingHtlcEvent.IncomingTimelock);
                existing.OutgoingTimelock = MergeNullable(existing.OutgoingTimelock, forwardingHtlcEvent.OutgoingTimelock);
                existing.IncomingAmountMsat = MergeNullable(existing.IncomingAmountMsat, forwardingHtlcEvent.IncomingAmountMsat);
                existing.OutgoingAmountMsat = MergeNullable(existing.OutgoingAmountMsat, forwardingHtlcEvent.OutgoingAmountMsat);
                existing.IncomingPeerAlias = MergeString(existing.IncomingPeerAlias, forwardingHtlcEvent.IncomingPeerAlias);
                existing.OutgoingPeerAlias = MergeString(existing.OutgoingPeerAlias, forwardingHtlcEvent.OutgoingPeerAlias);
                existing.FeeMsat = MergeNullable(existing.FeeMsat, forwardingHtlcEvent.FeeMsat);
                existing.GrossFeeMsat = MergeNullable(existing.GrossFeeMsat, forwardingHtlcEvent.GrossFeeMsat);
                existing.InboundFeeMsat = MergeNullable(existing.InboundFeeMsat, forwardingHtlcEvent.InboundFeeMsat);
                existing.RoutingFeePpm = MergeNullable(existing.RoutingFeePpm, forwardingHtlcEvent.RoutingFeePpm);
                existing.InboundFeePpm = MergeNullable(existing.InboundFeePpm, forwardingHtlcEvent.InboundFeePpm);
                existing.WireFailureCode = MergeNullable(existing.WireFailureCode, forwardingHtlcEvent.WireFailureCode);
                existing.FailureDetail = MergeNullable(existing.FailureDetail, forwardingHtlcEvent.FailureDetail);
                existing.FailureString = MergeString(existing.FailureString, forwardingHtlcEvent.FailureString);
                existing.UpdateDatetime = now;
            }

            await dbContext.SaveChangesAsync();

            return (true, null);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            _logger.LogDebug("Skipping duplicated forwarding HTLC event for node {ManagedNodePubKey}", forwardingHtlcEvent.ManagedNodePubKey);
            return (true, null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error saving forwarding HTLC event for node {ManagedNodePubKey}", forwardingHtlcEvent.ManagedNodePubKey);
            return (false, e.Message);
        }
    }

    private static T MergeEnum<T>(T currentValue, T newValue) where T : struct, Enum
    {
        return EqualityComparer<T>.Default.Equals(newValue, default) ? currentValue : newValue;
    }

    private static T? MergeNullable<T>(T? currentValue, T? newValue) where T : struct
    {
        return newValue ?? currentValue;
    }

    private static string? MergeString(string? currentValue, string? newValue)
    {
        return string.IsNullOrWhiteSpace(newValue) ? currentValue : newValue;
    }
}