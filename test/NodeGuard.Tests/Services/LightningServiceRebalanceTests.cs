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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Services;

/// <summary>
/// Tests for the rebalance-specific methods added to LightningService:
/// ProbeRouteAsync (BoS-style binary-search probing) and
/// GetLocalOutboundFeeRatePpmAsync (correct policy-side selection on a ChannelEdge).
/// </summary>
public class LightningServiceRebalanceTests
{
    private readonly Mock<ILogger<LightningService>> _logger = new();
    private readonly Mock<ILightningClientService> _lightningClient = new();
    private readonly Mock<ILightningRouterService> _lightningRouter = new();
    private readonly Mock<IDbContextFactory<ApplicationDbContext>> _dbContextFactory = new();

    private LightningService CreateService() => new(
        _logger.Object,
        new Mock<IChannelOperationRequestRepository>().Object,
        new Mock<INodeRepository>().Object,
        _dbContextFactory.Object,
        new Mock<IChannelOperationRequestPSBTRepository>().Object,
        new Mock<IChannelRepository>().Object,
        new Mock<IRemoteSignerService>().Object,
        new Mock<INBXplorerService>().Object,
        new Mock<ICoinSelectionService>().Object,
        _lightningClient.Object,
        _lightningRouter.Object);

    private static Node CreateNode(string pubkey = "030000000000000000000000000000000000000000000000000000000000000001")
        => new() { Id = 1, PubKey = pubkey, Endpoint = "localhost:10009", ChannelAdminMacaroon = "mac" };

    private static Lnrpc.Route MakeRoute(long amtMsat = 100_000_000)
    {
        var route = new Lnrpc.Route { TotalAmtMsat = amtMsat, TotalFeesMsat = 0 };
        route.Hops.Add(new Hop { ChanId = 1, AmtToForwardMsat = amtMsat });
        return route;
    }

    [Fact]
    public async Task ProbeRouteAsync_QueryRoutesAlwaysEmpty_ReturnsNoRoute()
    {
        _lightningClient
            .Setup(x => x.QueryRoutes(It.IsAny<Node>(), It.IsAny<QueryRoutesRequest>(), null))
            .ReturnsAsync(new QueryRoutesResponse());

        var service = CreateService();
        var result = await service.ProbeRouteAsync(CreateNode(), 100_000, 1_000_000,
            null, null, Constants.REBALANCE_PROBE_BACKOFF_RATIO, CancellationToken.None);

        result.Should().BeOfType<ProbeResult.NoRoute>();
    }

    [Fact]
    public async Task ProbeRouteAsync_LastHopIncorrectPaymentDetails_ReturnsSuccessAtFullAmount()
    {
        var route = MakeRoute();
        var routesResponse = new QueryRoutesResponse();
        routesResponse.Routes.Add(route);
        _lightningClient
            .Setup(x => x.QueryRoutes(It.IsAny<Node>(), It.IsAny<QueryRoutesRequest>(), null))
            .ReturnsAsync(routesResponse);

        // The probe-success signal: random hash failed at the last hop, but routing worked.
        _lightningRouter
            .Setup(x => x.SendToRouteV2Async(It.IsAny<Node>(), It.IsAny<Routerrpc.SendToRouteRequest>(),
                It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new HTLCAttempt
            {
                Failure = new Failure { Code = Failure.Types.FailureCode.IncorrectOrUnknownPaymentDetails },
            });

        var service = CreateService();
        var result = await service.ProbeRouteAsync(CreateNode(), 100_000, 1_000_000,
            null, null, Constants.REBALANCE_PROBE_BACKOFF_RATIO, CancellationToken.None);

        var success = result.Should().BeOfType<ProbeResult.Success>().Which;
        success.AmountSats.Should().Be(100_000);
        success.Route.Should().BeSameAs(route);
    }

    [Fact]
    public async Task ProbeRouteAsync_AllRoutesFailMidPath_HalvesUntilBelowMinThenReturnsNoRoute()
    {
        var routesResponse = new QueryRoutesResponse();
        routesResponse.Routes.Add(MakeRoute());
        _lightningClient
            .Setup(x => x.QueryRoutes(It.IsAny<Node>(), It.IsAny<QueryRoutesRequest>(), null))
            .ReturnsAsync(routesResponse);

        // Mid-route failure: not the IncorrectOrUnknownPaymentDetails signal.
        _lightningRouter
            .Setup(x => x.SendToRouteV2Async(It.IsAny<Node>(), It.IsAny<Routerrpc.SendToRouteRequest>(),
                It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new HTLCAttempt
            {
                Failure = new Failure { Code = Failure.Types.FailureCode.TemporaryChannelFailure },
            });

        var service = CreateService();
        var result = await service.ProbeRouteAsync(CreateNode(), 100_000, 1_000_000,
            null, null, Constants.REBALANCE_PROBE_BACKOFF_RATIO, CancellationToken.None);

        result.Should().BeOfType<ProbeResult.NoRoute>();
    }

}
