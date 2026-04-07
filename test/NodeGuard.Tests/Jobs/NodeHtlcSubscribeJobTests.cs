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
using Lnrpc;
using NodeGuard.Data.Models;
using NodeGuard.Jobs;
using Routerrpc;

namespace NodeGuard.Tests.Jobs;

public class NodeHtlcSubscribeJobTests
{
    private static readonly Node ManagedNode = new()
    {
        PubKey = "managed-pubkey",
        Name = "Managed Node",
    };

    [Fact]
    public void MapForwardingEvent_WhenForwardEvent_ReturnsSeedRowWithNetFee()
    {
        // Arrange
        var htlcEvent = new HtlcEvent
        {
            EventType = HtlcEvent.Types.EventType.Forward,
            TimestampNs = 1_735_689_600_123_456_700,
            IncomingChannelId = 11,
            OutgoingChannelId = 22,
            IncomingHtlcId = 33,
            OutgoingHtlcId = 44,
            ForwardEvent = new ForwardEvent
            {
                Info = new HtlcInfo
                {
                    IncomingAmtMsat = 1062,
                    OutgoingAmtMsat = 1000,
                }
            }
        };

        // Act
        var result = NodeHtlcSubscribeJob.MapForwardingEvent(ManagedNode, htlcEvent);

        // Assert
        result.Should().NotBeNull();
        result!.EventCase.Should().Be(HtlcEventCase.ForwardEvent);
        result.Outcome.Should().Be(ForwardingOutcome.Unknown);
        result.FeeMsat.Should().Be(62);
        result.ManagedNodePubKey.Should().Be("managed-pubkey");
        result.ManagedNodeName.Should().Be("Managed Node");
    }

    [Fact]
    public void ApplyFeeBreakdown_WhenInboundDiscountIsPresent_MapsGrossAndInboundFee()
    {
        // Arrange
        var forwardingHtlcEvent = new ForwardingHtlcEvent
        {
            IncomingAmountMsat = 1062,
            OutgoingAmountMsat = 1000,
        };

        var incomingPolicy = new RoutingPolicy
        {
            InboundFeeRateMilliMsat = -50000,
        };

        var outgoingPolicy = new RoutingPolicy
        {
            FeeBaseMsat = 20,
            FeeRateMilliMsat = 100000,
        };

        // Act
        NodeHtlcSubscribeJob.ApplyFeeBreakdown(forwardingHtlcEvent, incomingPolicy, outgoingPolicy);

        // Assert
        forwardingHtlcEvent.FeeMsat.Should().Be(62);
        forwardingHtlcEvent.GrossFeeMsat.Should().Be(120);
        forwardingHtlcEvent.InboundFeeMsat.Should().Be(-58);
        forwardingHtlcEvent.RoutingFeePpm.Should().Be(100000);
        forwardingHtlcEvent.InboundFeePpm.Should().Be(-50000);
    }

    [Fact]
    public void MapForwardingEvent_WhenSettleEvent_ReturnsSettledForwardingEvent()
    {
        // Arrange
        const ulong timestampNs = 1_735_689_600_123_456_700;
        var htlcEvent = new HtlcEvent
        {
            EventType = HtlcEvent.Types.EventType.Forward,
            TimestampNs = timestampNs,
            IncomingChannelId = 11,
            OutgoingChannelId = 22,
            IncomingHtlcId = 33,
            OutgoingHtlcId = 44,
            SettleEvent = new SettleEvent()
        };

        // Act
        var result = NodeHtlcSubscribeJob.MapForwardingEvent(ManagedNode, htlcEvent);

        // Assert
        result.Should().NotBeNull();
        result!.ManagedNodePubKey.Should().Be("managed-pubkey");
        result.ManagedNodeName.Should().Be("Managed Node");
        result.EventType.Should().Be(HtlcEventType.Forward);
        result.EventCase.Should().Be(HtlcEventCase.SettleEvent);
        result.Outcome.Should().Be(ForwardingOutcome.Settled);
        result.EventTimestamp.Should().Be(DateTimeOffset.UnixEpoch.AddTicks((long)(timestampNs / 100)));
    }

    [Fact]
    public void MapForwardingEvent_WhenForwardFailEvent_ReturnsFailedForwardingEvent()
    {
        // Arrange
        var htlcEvent = new HtlcEvent
        {
            EventType = HtlcEvent.Types.EventType.Forward,
            TimestampNs = 1_735_689_600_000_000_000,
            IncomingChannelId = 111,
            OutgoingChannelId = 222,
            IncomingHtlcId = 333,
            OutgoingHtlcId = 444,
            ForwardFailEvent = new ForwardFailEvent()
        };

        // Act
        var result = NodeHtlcSubscribeJob.MapForwardingEvent(ManagedNode, htlcEvent);

        // Assert
        result.Should().NotBeNull();
        result!.EventCase.Should().Be(HtlcEventCase.ForwardFailEvent);
        result.Outcome.Should().Be(ForwardingOutcome.Failed);
    }

    [Fact]
    public void MapForwardingEvent_WhenFinalHtlcEvent_ReturnsNull()
    {
        // Arrange
        var htlcEvent = new HtlcEvent
        {
            EventType = HtlcEvent.Types.EventType.Forward,
            TimestampNs = 1_735_689_600_000_000_000,
            IncomingChannelId = 9,
            OutgoingChannelId = 10,
            IncomingHtlcId = 11,
            OutgoingHtlcId = 12,
            FinalHtlcEvent = new FinalHtlcEvent
            {
                Settled = true,
                Offchain = false
            }
        };

        // Act
        var result = NodeHtlcSubscribeJob.MapForwardingEvent(ManagedNode, htlcEvent);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void MapForwardingEvent_WhenLinkFailEventHasInsufficientBalance_MapsLndFailureShape()
    {
        // Arrange
        var htlcEvent = new HtlcEvent
        {
            EventType = HtlcEvent.Types.EventType.Forward,
            TimestampNs = 1_735_689_600_000_000_000,
            IncomingChannelId = 1001,
            OutgoingChannelId = 1002,
            IncomingHtlcId = 1003,
            OutgoingHtlcId = 1004,
            LinkFailEvent = new LinkFailEvent
            {
                Info = new HtlcInfo
                {
                    IncomingTimelock = 144,
                    OutgoingTimelock = 128,
                    IncomingAmtMsat = 250000,
                    OutgoingAmtMsat = 249000
                },
                FailureDetail = FailureDetail.InsufficientBalance,
                WireFailure = Failure.Types.FailureCode.TemporaryChannelFailure,
                FailureString = "insufficient bandwidth to route htlc"
            }
        };

        // Act
        var result = NodeHtlcSubscribeJob.MapForwardingEvent(ManagedNode, htlcEvent);

        NodeHtlcSubscribeJob.ApplyFeeBreakdown(
            result!,
            new RoutingPolicy
            {
                InboundFeeRateMilliMsat = -50000,
            },
            new RoutingPolicy
            {
                FeeBaseMsat = 20,
                FeeRateMilliMsat = 100000,
            });

        // Assert
        result.Should().NotBeNull();
        result!.EventCase.Should().Be(HtlcEventCase.LinkFailEvent);
        result.Outcome.Should().Be(ForwardingOutcome.Failed);
        result.FailureDetail.Should().Be((int)FailureDetail.InsufficientBalance);
        result.WireFailureCode.Should().Be((int)Failure.Types.FailureCode.TemporaryChannelFailure);
        result.FailureString.Should().Be("insufficient bandwidth to route htlc");
        result.IncomingTimelock.Should().Be(144);
        result.OutgoingTimelock.Should().Be(128);
        result.IncomingAmountMsat.Should().Be(250000);
        result.OutgoingAmountMsat.Should().Be(249000);
        result.FeeMsat.Should().Be(1000);
        result.GrossFeeMsat.Should().Be(24920);
        result.InboundFeeMsat.Should().Be(-23920);
        result.RoutingFeePpm.Should().Be(100000);
        result.InboundFeePpm.Should().Be(-50000);
    }
}