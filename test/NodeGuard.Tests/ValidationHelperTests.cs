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
 */

using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;

namespace NodeGuard.Tests;

public class ValidationHelperTests
{
    [Theory]
    [InlineData("Vpub5jXH7agassYy8drZWSH1BbUdtpFvcSzWf9waRUUghPmRwNVucoudtAyCv5UPvFi9DyXxZtfiJUaduvTMKCtUxj8ARgD1vcPAQTasVSmCuxK", true)]
    [InlineData("tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLS", false)]

    public void ValidateXPUBTest(string xpub, bool expected)
    {
        //Arrange

        //Act
        var actual = ValidationHelper.ValidateXPUB(xpub);

        //Assert
        Assert.Equal(expected, actual);
    }

    private const int Loop = 10;
    private const int Boltz = 11;
    private const int OwnNode = 1;

    private static ReservedChannelOpenPeer Reserved(int peerNodeId, int pct = 20)
        => new() { PeerNodeId = peerNodeId, BudgetPct = pct };

    [Fact]
    public void ValidateReservedChannelOpenPeers_SharesUpToAHundredPercent_Pass()
        => ValidationHelper.ValidateReservedChannelOpenPeers([Reserved(Loop, 60), Reserved(Boltz, 40)], OwnNode)
            .Should().BeNull();

    [Fact]
    public void ValidateReservedChannelOpenPeers_NoNodeChosen_Fails()
        => ValidationHelper.ValidateReservedChannelOpenPeers([Reserved(0)], OwnNode)
            .Should().Contain("Choose a node");

    [Fact]
    public void ValidateReservedChannelOpenPeers_TheNodeItself_Fails()
        => ValidationHelper.ValidateReservedChannelOpenPeers([Reserved(OwnNode)], OwnNode)
            .Should().Contain("itself");

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void ValidateReservedChannelOpenPeers_ShareOutOfRange_Fails(int pct)
        => ValidationHelper.ValidateReservedChannelOpenPeers([Reserved(Loop, pct)], OwnNode)
            .Should().Contain("between 1 and 100%");

    [Fact]
    public void ValidateReservedChannelOpenPeers_SharesAboveAHundredPercent_Fail()
        => ValidationHelper.ValidateReservedChannelOpenPeers([Reserved(Loop, 60), Reserved(Boltz, 41)], OwnNode)
            .Should().Contain("101%");

    [Fact]
    public void ValidateReservedChannelOpenPeers_SamePeerTwice_Fails()
        => ValidationHelper.ValidateReservedChannelOpenPeers([Reserved(Loop, 10), Reserved(Loop, 10)], OwnNode)
            .Should().Contain("more than once");




}