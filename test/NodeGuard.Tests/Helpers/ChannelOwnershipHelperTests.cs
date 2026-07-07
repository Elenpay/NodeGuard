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

using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;

namespace NodeGuard.Tests;

public class ChannelOwnershipHelperTests
{
    private const string NodeAPubKey = "02aaaa";
    private const string NodeBPubKey = "02bbbb";
    private const string UnmanagedPubKey = "03cccc";

    private static readonly IReadOnlyCollection<Node> ManagedNodes = new List<Node>
    {
        new() { PubKey = NodeAPubKey },
        new() { PubKey = NodeBPubKey },
    };

    [Fact]
    public void ChannelBetweenTwoManagedNodes_IsOwnedByExactlyTheInitiatorSide()
    {
        // Same channel seen from each side. A opened it.
        var fromA = new Lnrpc.Channel { Initiator = true, RemotePubkey = NodeBPubKey };
        var fromB = new Lnrpc.Channel { Initiator = false, RemotePubkey = NodeAPubKey };

        ChannelOwnershipHelper.IsOwnedByManagedNode(fromA, ManagedNodes).Should().BeTrue();
        ChannelOwnershipHelper.IsOwnedByManagedNode(fromB, ManagedNodes).Should().BeFalse();
    }

    [Fact]
    public void NonInitiatorChannel_ToUnmanagedPeer_IsOwned()
    {
        // We didn't open it, but the peer isn't managed — no other side will report it, so we own it.
        var channel = new Lnrpc.Channel { Initiator = false, RemotePubkey = UnmanagedPubKey };

        ChannelOwnershipHelper.IsOwnedByManagedNode(channel, ManagedNodes).Should().BeTrue();
    }

    [Fact]
    public void InitiatorChannel_IsAlwaysOwned()
    {
        var channel = new Lnrpc.Channel { Initiator = true, RemotePubkey = UnmanagedPubKey };

        ChannelOwnershipHelper.IsOwnedByManagedNode(channel, ManagedNodes).Should().BeTrue();
    }
}
