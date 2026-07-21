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

namespace NodeGuard.Helpers;

public static class ChannelOwnershipHelper
{
    /// <summary>
    /// Returns false if not initiator and peer is a managed node (dedup rule), true otherwise.
    /// </summary>
    public static bool IsOwnedByManagedNode(
        Lnrpc.Channel lndChannel,
        IReadOnlyCollection<Node> allManagedNodes)
    {
        if (lndChannel == null) throw new ArgumentNullException(nameof(lndChannel));
        if (allManagedNodes == null) throw new ArgumentNullException(nameof(allManagedNodes));

        if (!lndChannel.Initiator &&
            allManagedNodes.Any(n => n.PubKey == lndChannel.RemotePubkey))
        {
            return false;
        }

        return true;
    }
}
