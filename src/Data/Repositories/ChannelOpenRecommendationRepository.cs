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
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories;

public class ChannelOpenRecommendationRepository : IChannelOpenRecommendationRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<ChannelOpenRecommendationRepository> _logger;

    public ChannelOpenRecommendationRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILogger<ChannelOpenRecommendationRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<ChannelOpenRecommendation?> GetById(int id)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ChannelOpenRecommendations
            .Include(x => x.Node)
            .Include(x => x.ChannelOperationRequest)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<List<ChannelOpenRecommendation>> GetOpenByNode(int nodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ChannelOpenRecommendations
            .Where(x => x.NodeId == nodeId && x.Status == ChannelOpenRecommendationStatus.Open)
            .OrderByDescending(x => x.MissedFeeMsat)
            .ToListAsync();
    }

    public async Task<(ChannelOpenRecommendation? Persisted, string? Error)> Upsert(ChannelOpenRecommendation recommendation)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var existing = await context.ChannelOpenRecommendations
                .Include(x => x.ChannelOperationRequest)
                .FirstOrDefaultAsync(x => x.NodeId == recommendation.NodeId
                                          && x.PeerPubKey == recommendation.PeerPubKey);

            if (existing == null)
            {
                // Insert via FK only — never let the Node nav try to insert/attach a Node.
                recommendation.Node = null;
                recommendation.SetCreationDatetime();
                recommendation.SetUpdateDatetime();
                await context.ChannelOpenRecommendations.AddAsync(recommendation);
                await context.SaveChangesAsync();

                return (recommendation, null);
            }

            // A plan only reaches this point for a peer already past its cooldown, so the one reason
            // left to hold a row back is a promotion whose channel open has not resolved yet.
            if (existing.Status == ChannelOpenRecommendationStatus.Promoted
                && existing.ChannelOperationRequest is { IsFinalized: false })
            {
                return (existing, null);
            }

            CopyEvidence(from: recommendation, to: existing);
            existing.Status = ChannelOpenRecommendationStatus.Open;

            // The row is being recycled, so the previous decision's trail would misrepresent it. The
            // promotion itself survives on the request and in the audit log.
            existing.ChannelOperationRequestId = null;
            existing.ChannelOperationRequest = null;
            existing.DismissReason = null;
            existing.SetUpdateDatetime();
            await context.SaveChangesAsync();

            return (existing, null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error upserting channel open recommendation for node {NodeId} peer {Peer}",
                recommendation.NodeId, recommendation.PeerPubKey);
            return (null, e.Message);
        }
    }

    /// <summary>
    /// Identity, lifecycle and the promotion link belong to the stored row and are deliberately not
    /// copied.
    /// </summary>
    private static void CopyEvidence(ChannelOpenRecommendation from, ChannelOpenRecommendation to)
    {
        to.PeerAlias = from.PeerAlias;
        to.OutgoingChannelId = from.OutgoingChannelId;
        to.ChannelsInvolved = from.ChannelsInvolved;
        to.Bursts = from.Bursts;
        to.MissedFeeMsat = from.MissedFeeMsat;
        to.FailedAttempts = from.FailedAttempts;
        to.OutSats = from.OutSats;
        to.InSats = from.InSats;
        to.MissedSats = from.MissedSats;
        to.MaxBurstPaymentSats = from.MaxBurstPaymentSats;
        to.Regime = from.Regime;
        to.SuggestedCapacitySats = from.SuggestedCapacitySats;
        to.BindingClamp = from.BindingClamp;
        to.InheritedFeeRatePpm = from.InheritedFeeRatePpm;
        to.InheritedBaseFeeMsat = from.InheritedBaseFeeMsat;
        to.LastEvidenceAt = from.LastEvidenceAt;
    }

    public async Task<(bool, string?)> Update(ChannelOpenRecommendation recommendation)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            recommendation.SetUpdateDatetime();
            recommendation.Node = null;
            recommendation.ChannelOperationRequest = null;
            context.ChannelOpenRecommendations.Update(recommendation);
            await context.SaveChangesAsync();

            return (true, null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error updating channel open recommendation {Id}", recommendation.Id);
            return (false, e.Message);
        }
    }

    public async Task<int> ExpireOpen(int nodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ChannelOpenRecommendations
            .Where(x => x.NodeId == nodeId
                        && x.Status == ChannelOpenRecommendationStatus.Open)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ChannelOpenRecommendationStatus.Expired)
                .SetProperty(x => x.UpdateDatetime, DateTimeOffset.UtcNow));
    }

    public async Task<int> FailUnrealizedPromotions(int nodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Rejected is an operator turning the peer down and Cancelled is not, so only the latter joins
        // Failed here — a rejection suppresses the peer exactly as a dismissal does.
        var unrealized = await context.ChannelOpenRecommendations
            .Where(x => x.NodeId == nodeId
                        && x.Status == ChannelOpenRecommendationStatus.Promoted
                        && x.ChannelOperationRequest != null
                        && (x.ChannelOperationRequest.Status == ChannelOperationRequestStatus.Failed
                            || x.ChannelOperationRequest.Status == ChannelOperationRequestStatus.Cancelled))
            .ToListAsync();

        if (unrealized.Count == 0)
        {
            return 0;
        }

        foreach (var recommendation in unrealized)
        {
            recommendation.Status = ChannelOpenRecommendationStatus.Failed;
            recommendation.SetUpdateDatetime();
        }

        await context.SaveChangesAsync();

        return unrealized.Count;
    }

    public async Task<Dictionary<string, DateTimeOffset>> GetLastDecisionByPeer(int nodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ChannelOpenRecommendations
            .Where(x => x.NodeId == nodeId
                        && (x.Status == ChannelOpenRecommendationStatus.Promoted
                            || x.Status == ChannelOpenRecommendationStatus.Dismissed))
            .GroupBy(x => x.PeerPubKey)
            .Select(g => new { PeerPubKey = g.Key, Last = g.Max(x => x.UpdateDatetime) })
            .ToDictionaryAsync(x => x.PeerPubKey, x => x.Last);
    }
}
