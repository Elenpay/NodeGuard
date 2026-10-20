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
        _sparkSwaps.ExitBatchAsync(Arg.Any<IReadOnlyList<SwapOut>>(), Arg.Any<CancellationToken>()).Returns(SparkExitStep.ExitSent);
        Plans();
    }

    private SparkSwapExitJob Job(bool sparkEnabled = true) => new(NullLogger<SparkSwapExitJob>.Instance, _swapOuts, _sparkSwaps,
        _audit, sparkEnabled ? new SparkSettings { Enabled = true } : SparkSettings.Disabled);

    private static SwapOut Swap(int id, int minutes, long? lightningFee = 10, string? transferId = null,
        SwapProvider provider = SwapProvider.Spark, string? address = null) => new()
    {
        Id = id,
        Provider = provider,
        ProviderId = $"req-{id}",
        Status = SwapOutStatus.Pending,
        SatsAmount = 100_000,
        LightningFeeSats = lightningFee,
        SparkTransferId = transferId,
        SparkIdentity = "02aa",
        DestinationWalletId = 3,
        DestinationAddress = address,
        CreationDatetime = DateTimeOffset.UnixEpoch.AddMinutes(minutes)
    };

    /// <summary>The batches the service plans to exit now.</summary>
    private void Plans(params IReadOnlyList<SwapOut>[] batches) =>
        _sparkSwaps.PlanExitsAsync(Arg.Any<IReadOnlyCollection<SwapOut>>(), Arg.Any<CancellationToken>())
            .Returns(batches.ToList());

    private static IReadOnlyList<SwapOut> Batch(params SwapOut[] swaps) =>
        Arg.Is<IReadOnlyList<SwapOut>>(b => b.SequenceEqual(swaps));

    [Fact]
    public async Task Execute_AttributesEveryPaidSwap_FollowsTheBatchesExiting_ThenExitsThePlannedOnes()
    {
        var exitingOlder = Swap(1, minutes: 1, transferId: "transfer-1", address: "bcrt1-batch");
        var exitingNewer = Swap(3, minutes: 3, transferId: "transfer-3", address: "bcrt1-batch");
        var waiting = Swap(2, minutes: 5);
        var unpaid = Swap(4, minutes: 0, lightningFee: null);
        var loop = Swap(5, minutes: 0, provider: SwapProvider.Loop);
        var pending = new List<SwapOut> { waiting, unpaid, exitingNewer, loop, exitingOlder };
        _swapOuts.GetAllPending().Returns(pending);
        Plans(new List<SwapOut> { waiting });

        await Job().Execute(_context);

        Received.InOrder(() =>
        {
            _sparkSwaps.AttributeAsync(waiting, Arg.Any<CancellationToken>());
            _sparkSwaps.ExitBatchAsync(Batch(exitingOlder, exitingNewer), Arg.Any<CancellationToken>());
            _sparkSwaps.PlanExitsAsync(pending, Arg.Any<CancellationToken>());
            _sparkSwaps.ExitBatchAsync(Batch(waiting), Arg.Any<CancellationToken>());
        });
        await _sparkSwaps.DidNotReceive().AttributeAsync(exitingOlder, Arg.Any<CancellationToken>());
        await _sparkSwaps.Received(2).ExitBatchAsync(Arg.Any<IReadOnlyList<SwapOut>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_AuditsEverySwapOfACompletedBatch()
    {
        var first = Swap(1, minutes: 1, transferId: "transfer-1", address: "bcrt1-batch");
        var second = Swap(2, minutes: 2, transferId: "transfer-2", address: "bcrt1-batch");
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { first, second });
        _sparkSwaps.ExitBatchAsync(Batch(first, second), Arg.Any<CancellationToken>()).Returns(SparkExitStep.Completed);

        await Job().Execute(_context);

        await _audit.Received(1).LogSystemAsync(AuditActionType.SwapOutCompleted, AuditEventType.Success, AuditObjectType.SwapOut,
            "req-1", Arg.Any<object?>());
        await _audit.Received(1).LogSystemAsync(AuditActionType.SwapOutCompleted, AuditEventType.Success, AuditObjectType.SwapOut,
            "req-2", Arg.Any<object?>());
    }

    [Fact]
    public async Task Execute_ABatchThatFails_DoesNotStopTheOthers()
    {
        var failing = Swap(1, minutes: 1, transferId: "transfer-1", address: "bcrt1-one");
        var next = Swap(2, minutes: 2, transferId: "transfer-2", address: "bcrt1-two");
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { failing, next });
        _sparkSwaps.ExitBatchAsync(Batch(failing), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("the SSP is down"));

        await Job().Execute(_context);

        await _sparkSwaps.Received(1).ExitBatchAsync(Batch(next), Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().LogSystemAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Execute_WhenPlanningFails_StillFollowsTheBatchesExiting()
    {
        var exiting = Swap(1, minutes: 1, transferId: "transfer-1", address: "bcrt1-batch");
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { exiting });
        _sparkSwaps.PlanExitsAsync(Arg.Any<IReadOnlyCollection<SwapOut>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the wallet is down"));

        await Job().Execute(_context);

        await _sparkSwaps.Received(1).ExitBatchAsync(Batch(exiting), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_WithSparkDisabled_DoesNothing()
    {
        await Job(sparkEnabled: false).Execute(_context);

        await _swapOuts.DidNotReceive().GetAllPending();
    }
}
