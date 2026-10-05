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
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services.Spark;
using NSubstitute;

namespace NodeGuard.Services;

public class SwapsServiceTests
{
    private readonly ILoopService _loop = Substitute.For<ILoopService>();
    private readonly IFortySwapService _fortySwap = Substitute.For<IFortySwapService>();
    private readonly ISwapOutRepository _swapOuts = Substitute.For<ISwapOutRepository>();
    private readonly ISparkSwapService _spark = Substitute.For<ISparkSwapService>();
    private readonly Node _node = new() { Id = 7, Name = "alice", PubKey = "02aa", Endpoint = "alice:10009" };

    private SwapsService Service() => new(_loop, _fortySwap, Substitute.For<ILightningService>(), _swapOuts, _spark);

    private static SwapOutRequest Request() => new() { Amount = 1_000_000, Address = "bcrt1qdestination" };

    [Fact]
    public async Task CreateSwapOut_RecordsTheSwapWithTheProvidersResult()
    {
        _loop.CreateSwapOutAsync(_node, Arg.Any<SwapOutRequest>(), Arg.Any<CancellationToken>()).Returns(new SwapResponse
        {
            Id = "ABCDEF",
            HtlcAddress = "bcrt1phtlc",
            PaymentHash = "abcdef",
            Amount = 1_000_000,
            ServerFee = 300,
            OnchainFee = 200,
            OffchainFee = 100,
            Status = SwapOutStatus.Pending
        });
        _swapOuts.AddAsync(Arg.Any<SwapOut>()).Returns((true, null));
        var swapOut = new SwapOut { Provider = SwapProvider.Loop, NodeId = 7, SatsAmount = 1_000_000, IsManual = true };

        var (response, saved, error) = await Service().CreateSwapOutAsync(_node, swapOut, Request());

        saved.Should().BeTrue();
        error.Should().BeNull();
        response.Id.Should().Be("ABCDEF");
        await _swapOuts.Received(1).AddAsync(Arg.Is<SwapOut>(s =>
            s == swapOut &&
            s.ProviderId == "ABCDEF" &&
            s.Status == SwapOutStatus.Pending &&
            s.ServiceFeeSats == 300 &&
            s.OnChainFeeSats == 200 &&
            s.LightningFeeSats == 100 &&
            s.DestinationAddress == "bcrt1qdestination" &&
            s.PaymentHash == "abcdef" &&
            s.SatsAmount == 1_000_000 &&
            s.IsManual));
    }

    [Fact]
    public async Task CreateSwapOut_ReportsARecordThatCouldNotBeSaved()
    {
        _fortySwap.CreateSwapOutAsync(_node, Arg.Any<SwapOutRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SwapResponse { Id = "forty-1", HtlcAddress = string.Empty, Status = SwapOutStatus.Pending });
        _swapOuts.AddAsync(Arg.Any<SwapOut>()).Returns((false, "database is down"));

        var (response, saved, error) = await Service().CreateSwapOutAsync(_node,
            new SwapOut { Provider = SwapProvider.FortySwap, NodeId = 7 }, Request());

        response.Id.Should().Be("forty-1");
        saved.Should().BeFalse();
        error.Should().Be("database is down");
    }

    [Fact]
    public async Task CreateSwapOut_LeavesSparkSwapsToTheSparkProvider_WhichRecordsThemBeforePaying()
    {
        var swapOut = new SwapOut { Provider = SwapProvider.Spark, NodeId = 7 };
        var creation = new SwapOutCreation(new SwapResponse { Id = "req-1", HtlcAddress = string.Empty }, true, null);
        _spark.CreateSwapOutAsync(_node, swapOut, Arg.Any<SwapOutRequest>(), Arg.Any<CancellationToken>()).Returns(creation);

        (await Service().CreateSwapOutAsync(_node, swapOut, Request())).Should().BeSameAs(creation);

        await _swapOuts.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }
}
