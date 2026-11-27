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

namespace NodeGuard.Services;

/// <summary>
/// What is left of a node's channel open budget for the current period, split into one share per
/// reserved peer and a general part for everyone else. A reserved peer spends its own share first and
/// then the general part; any other peer only ever spends the general part. Pure, no I/O.
/// </summary>
public class ChannelOpenBudget
{
    private readonly Dictionary<string, long> _reservedLeftByPeer;
    private long _generalLeft;

    private ChannelOpenBudget(Dictionary<string, long> reservedLeftByPeer, long generalLeft)
    {
        _reservedLeftByPeer = reservedLeftByPeer;
        _generalLeft = generalLeft;
    }

    /// <param name="budgetSats">The node's budget for the period.</param>
    /// <param name="reservedPctByPeer">Each reserved peer's share of it, in percent.</param>
    /// <param name="committedByPeer">What the period has already committed, per peer.</param>
    public static ChannelOpenBudget Build(
        long budgetSats,
        IReadOnlyDictionary<string, int> reservedPctByPeer,
        IReadOnlyDictionary<string, long> committedByPeer)
    {
        var reservedLeftByPeer = new Dictionary<string, long>();
        var generalSats = budgetSats;
        var generalCommitted = 0L;

        foreach (var (peer, pct) in reservedPctByPeer)
        {
            var shareSats = budgetSats * Math.Clamp(pct, 0, 100) / 100;
            var committed = committedByPeer.GetValueOrDefault(peer);
            generalSats -= shareSats;
            reservedLeftByPeer[peer] = Math.Max(0, shareSats - committed);
            // What a reserved peer spent past its own share came out of the general part.
            generalCommitted += Math.Max(0, committed - shareSats);
        }

        generalCommitted += committedByPeer
            .Where(x => !reservedPctByPeer.ContainsKey(x.Key))
            .Sum(x => x.Value);

        return new ChannelOpenBudget(reservedLeftByPeer, Math.Max(0, generalSats) - generalCommitted);
    }

    public bool IsReserved(string peerPubKey) => _reservedLeftByPeer.ContainsKey(peerPubKey);

    public long HeadroomFor(string peerPubKey) =>
        _reservedLeftByPeer.GetValueOrDefault(peerPubKey) + Math.Max(0, _generalLeft);

    /// <summary>Whether any peer, reserved or not, could still be given anything.</summary>
    public bool IsExhausted => _generalLeft <= 0 && _reservedLeftByPeer.Values.All(x => x <= 0);

    public void Spend(string peerPubKey, long sats)
    {
        var fromReserved = Math.Min(_reservedLeftByPeer.GetValueOrDefault(peerPubKey), sats);
        if (fromReserved > 0)
        {
            _reservedLeftByPeer[peerPubKey] -= fromReserved;
        }

        _generalLeft -= sats - fromReserved;
    }
}
