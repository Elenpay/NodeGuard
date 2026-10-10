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

using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using NodeGuard.Services.Spark;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;

namespace NodeGuard.Jobs;

public class SparkSwapExitJobTests
{
    private readonly ISwapOutRepository _swapOuts = Substitute.For<ISwapOutRepository>();
    private readonly ISparkSwapService _sparkSwaps = Substitute.For<ISparkSwapService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IJobExecutionContext _context = Substitute.For<IJobExecutionContext>();

    public SparkSwapExitJobTests()
    {
        _context.CancellationToken.Returns(CancellationToken.None);
        _sparkSwaps.AttributeAsync(Arg.Any<SwapOut>(), Arg.Any<CancellationToken>()).Returns(true);
        _sparkSwaps.ExitAsync(Arg.Any<SwapOut>(), Arg.Any<CancellationToken>()).Returns(SparkExitStep.ExitSent);
    }

    private SparkSwapExitJob Job(bool sparkEnabled = true) => new(NullLogger<SparkSwapExitJob>.Instance, _swapOuts, _sparkSwaps,
        _audit, sparkEnabled ? new SparkSettings { Enabled = true } : SparkSettings.Disabled);

    private static SwapOut Swap(int id, int minutes, long? lightningFee = 10, string? transferId = null,
        SwapProvider provider = SwapProvider.Spark) => new()
    {
        Id = id,
        Provider = provider,
        ProviderId = $"req-{id}",
        Status = SwapOutStatus.Pending,
        SatsAmount = 100_000,
        LightningFeeSats = lightningFee,
        SparkTransferId = transferId,
        CreationDatetime = DateTimeOffset.UnixEpoch.AddMinutes(minutes)
    };

    [Fact]
    public async Task Execute_AttributesEveryPaidSwap_ThenExitsThemOneAtATime_OldestFirst()
    {
        var newer = Swap(2, minutes: 5);
        var older = Swap(1, minutes: 1);
        var attributed = Swap(3, minutes: 3, transferId: "transfer-3");
        var unpaid = Swap(4, minutes: 0, lightningFee: null);
        var loop = Swap(5, minutes: 0, provider: SwapProvider.Loop);
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { newer, unpaid, attributed, loop, older });

        await Job().Execute(_context);

        Received.InOrder(() =>
        {
            _sparkSwaps.AttributeAsync(older, Arg.Any<CancellationToken>());
            _sparkSwaps.AttributeAsync(newer, Arg.Any<CancellationToken>());
            _sparkSwaps.ExitAsync(older, Arg.Any<CancellationToken>());
            _sparkSwaps.ExitAsync(attributed, Arg.Any<CancellationToken>());
            _sparkSwaps.ExitAsync(newer, Arg.Any<CancellationToken>());
        });
        await _sparkSwaps.DidNotReceive().AttributeAsync(attributed, Arg.Any<CancellationToken>());
        await _sparkSwaps.DidNotReceive().ExitAsync(unpaid, Arg.Any<CancellationToken>());
        await _sparkSwaps.DidNotReceive().ExitAsync(loop, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_AuditsACompletedSwap()
    {
        var swap = Swap(1, minutes: 1, transferId: "transfer-1");
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { swap });
        _sparkSwaps.ExitAsync(swap, Arg.Any<CancellationToken>()).Returns(SparkExitStep.Completed);

        await Job().Execute(_context);

        await _audit.Received(1).LogSystemAsync(AuditActionType.SwapOutCompleted, AuditEventType.Success, AuditObjectType.SwapOut,
            "req-1", Arg.Any<object?>());
    }

    [Fact]
    public async Task Execute_ASwapThatFails_DoesNotStopTheOthers()
    {
        var failing = Swap(1, minutes: 1, transferId: "transfer-1");
        var next = Swap(2, minutes: 2, transferId: "transfer-2");
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { failing, next });
        _sparkSwaps.ExitAsync(failing, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("the SSP is down"));

        await Job().Execute(_context);

        await _sparkSwaps.Received(1).ExitAsync(next, Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().LogSystemAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Execute_WithSparkDisabled_DoesNothing()
    {
        await Job(sparkEnabled: false).Execute(_context);

        await _swapOuts.DidNotReceive().GetAllPending();
    }
}
