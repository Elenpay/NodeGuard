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
using Grpc.Core;
using Lnrpc;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.TestHelpers;

namespace NodeGuard.Services;

public class LightningClientServiceTests
{
    [Fact]
    public async Task CreateLightningClient_EndpointIsNull()
    {
        // Arrange
        var logger = new Mock<ILogger<LightningClientService>>();
        var lightningClientService = new LightningClientService(logger.Object);

        // Act
        var act = () => lightningClientService.GetLightningClient(null);

        // Assert
        act
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("Value cannot be null. (Parameter 'endpoint')");
    }

    [Fact]
    public void CreateLightningClient_ReturnsLightningClient()
    {
        // Arrange
        var logger = new Mock<ILogger<LightningClientService>>();
        var lightningClientService = new LightningClientService(logger.Object);

        // Act
        var result = lightningClientService.GetLightningClient("10.0.0.1");

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task SetChannelFeePolicy_BuildsPolicyUpdateRequestWithInboundFee()
    {
        // Arrange
        var logger = new Mock<ILogger<LightningClientService>>();
        var lightningClientService = new LightningClientService(logger.Object);
        var lightningClient = new Mock<Lightning.LightningClient>();
        var chanPoint = NBitcoin.OutPoint.Parse("0000000000000000000000000000000000000000000000000000000000000001:2");
        var node = new Node
        {
            Endpoint = "127.0.0.1:10009",
            ChannelAdminMacaroon = "test-macaroon"
        };

        PolicyUpdateRequest? capturedRequest = null;
        Metadata? capturedMetadata = null;

        lightningClient
            .Setup(x => x.UpdateChannelPolicyAsync(
                It.IsAny<PolicyUpdateRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<PolicyUpdateRequest, Metadata, DateTime?, CancellationToken>((request, metadata, _, _) =>
            {
                capturedRequest = request;
                capturedMetadata = metadata;
            })
            .Returns(MockHelpers.CreateAsyncUnaryCall(new PolicyUpdateResponse()));

        // Act
        var response = await lightningClientService.SetChannelFeePolicy(
            node,
            chanPoint,
            baseFeeMsat: 1000,
            feeRatePpm: 250,
            timeLockDelta: 40,
            inboundBaseFeeMsat: -100,
            inboundFeeRatePpm: -25,
            lightningClient.Object);

        // Assert
        response.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ChanPoint.FundingTxidStr.Should().Be(chanPoint.Hash.ToString());
        capturedRequest.ChanPoint.OutputIndex.Should().Be(chanPoint.N);
        capturedRequest.BaseFeeMsat.Should().Be(1000);
        capturedRequest.FeeRatePpm.Should().Be(250);
        capturedRequest.TimeLockDelta.Should().Be(40);
        capturedRequest.InboundFee.Should().NotBeNull();
        capturedRequest.InboundFee.BaseFeeMsat.Should().Be(-100);
        capturedRequest.InboundFee.FeeRatePpm.Should().Be(-25);
        capturedMetadata.Should().ContainSingle(entry => entry.Key == "macaroon" && entry.Value == "test-macaroon");
    }

    [Fact]
    public async Task SetChannelFeePolicy_WithoutInboundFee_DoesNotSetInboundFee()
    {
        // Arrange
        var logger = new Mock<ILogger<LightningClientService>>();
        var lightningClientService = new LightningClientService(logger.Object);
        var lightningClient = new Mock<Lightning.LightningClient>();
        var chanPoint = NBitcoin.OutPoint.Parse("0000000000000000000000000000000000000000000000000000000000000001:2");
        var node = new Node
        {
            Endpoint = "127.0.0.1:10009",
            ChannelAdminMacaroon = "test-macaroon"
        };

        PolicyUpdateRequest? capturedRequest = null;

        lightningClient
            .Setup(x => x.UpdateChannelPolicyAsync(
                It.IsAny<PolicyUpdateRequest>(),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<PolicyUpdateRequest, Metadata, DateTime?, CancellationToken>((request, _, _, _) =>
            {
                capturedRequest = request;
            })
            .Returns(MockHelpers.CreateAsyncUnaryCall(new PolicyUpdateResponse()));

        // Act
        await lightningClientService.SetChannelFeePolicy(
            node,
            chanPoint,
            baseFeeMsat: 1000,
            feeRatePpm: 250,
            timeLockDelta: 40,
            inboundBaseFeeMsat: null,
            inboundFeeRatePpm: null,
            lightningClient.Object);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.InboundFee.Should().BeNull();
    }
}
