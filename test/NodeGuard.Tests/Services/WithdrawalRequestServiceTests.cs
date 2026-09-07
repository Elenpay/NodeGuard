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
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using Quartz;
using Key = NodeGuard.Data.Models.Key;

namespace NodeGuard.Services;

/// <summary>
/// WithdrawalRequestService carries the RBF bump flow shared by the Withdrawals page and the BumpWithdrawal gRPC
/// method, and the hot-wallet execution step both use. The status assertions matter: PerformWithdrawal refuses anything
/// but PSBTSignaturesPending / FinalizingPSBT, and a hot wallet has no approvers whose signatures would move a request
/// there, so a hot-wallet request left in Pending fails inside the job.
/// </summary>
public class WithdrawalRequestServiceTests
{
    private const string Tpub =
        "tpubDCfM7v7fKZ31gTGGggNMycfCr5cDGinyijveRZ44RYSgAgEARwhaBd6PPpWst8kKbhEVoqNasgjHFWZKrEQoJ9pzPVEmNZDNe92hShzEMDy";

    private const string DestinationAddress = "bcrt1q590shaxaf5u08ml8jwlzghz99dup3z9592vxal";
    private const string ReturnAddress = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf";
    private const int OriginalId = 10;
    private const int BumpId = 11;

    private readonly Mock<IWalletWithdrawalRequestRepository> _requests = new();
    private readonly Mock<IFMUTXORepository> _fmutxos = new();
    private readonly Mock<ICoinSelectionService> _coinSelection = new();
    private readonly Mock<INBXplorerService> _nbXplorer = new();
    private readonly Mock<IBitcoinService> _bitcoin = new();
    private readonly Mock<ISchedulerFactory> _schedulerFactory = new();
    private readonly Mock<IScheduler> _scheduler = new();

    private readonly uint256 _fundingTxId = uint256.Parse("3a9907ab1b965e1ad0024e7fe4466ddab0d3b1bf3bd15ab6416bf8f0c9032f84");
    private readonly uint256 _originalTxId = uint256.Parse("f103e12f02ac1e5b8826831d4fc8fdb78a707bd00c4e1f191fe5d14458d63d5a");

    private WalletWithdrawalRequest? _storedBump;

    public WithdrawalRequestServiceTests()
    {
        _requests.Setup(x => x.AddAsync(It.IsAny<WalletWithdrawalRequest>()))
            .Callback<WalletWithdrawalRequest>(r =>
            {
                r.Id = BumpId;
                _storedBump = r;
            })
            .ReturnsAsync((true, (string?)null));
        _requests.Setup(x => x.GetById(BumpId)).ReturnsAsync(() => _storedBump);
        _requests.Setup(x => x.Update(It.IsAny<WalletWithdrawalRequest>())).Returns((true, (string?)null));

        _coinSelection.Setup(x => x.GetUTXOsByOutpointAsync(It.IsAny<DerivationStrategyBase>(), It.IsAny<List<OutPoint>>()))
            .ReturnsAsync((DerivationStrategyBase _, List<OutPoint> outpoints) =>
                outpoints.Select(o => new UTXO { Outpoint = o, Value = Money.Satoshis(10_000_000) }).ToList());

        _schedulerFactory.Setup(x => x.GetScheduler(It.IsAny<CancellationToken>())).ReturnsAsync(_scheduler.Object);
        _scheduler.Setup(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow);

        _bitcoin.Setup(x => x.GenerateTemplatePSBT(It.IsAny<WalletWithdrawalRequest>())).ReturnsAsync(EmptyPsbt());

        _nbXplorer.Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Deposit, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KeyPathInformation { Address = BitcoinAddress.Create(ReturnAddress, Network.RegTest) });
    }

    private WithdrawalRequestService CreateService() => new(
        new Mock<ILogger<WithdrawalRequestService>>().Object,
        _requests.Object,
        _fmutxos.Object,
        _coinSelection.Object,
        _nbXplorer.Object,
        _bitcoin.Object,
        _schedulerFactory.Object);

    // ---- CreateBumpRequestAsync ----------------------------------------------------------------------------------

    [Fact]
    public async Task CreateBumpRequestAsync_HotWallet_CreatesTheBumpInPsbtSignaturesPending()
    {
        var original = SetupOriginal(HotWallet());

        var bump = await CreateService().CreateBumpRequestAsync(OriginalId, MempoolRecommendedFeesType.CustomFee, 10m);

        bump.Should().BeSameAs(_storedBump);
        bump.Status.Should().Be(WalletWithdrawalRequestStatus.PSBTSignaturesPending,
            "a hot wallet has no approvers, and PerformWithdrawal refuses a Pending request");
        bump.BumpingWalletWithdrawalRequestId.Should().Be(OriginalId);
        bump.WalletId.Should().Be(original.WalletId);
        bump.Description.Should().Be($"Bump of request {OriginalId}: pay rent");
        bump.MempoolRecommendedFeesType.Should().Be(MempoolRecommendedFeesType.CustomFee);
        bump.CustomFeeRate.Should().Be(10m);
        bump.Changeless.Should().Be(original.Changeless);
        bump.WithdrawAllFunds.Should().Be(original.WithdrawAllFunds);
        bump.UserRequestorId.Should().Be(original.UserRequestorId);
        bump.RequestMetadata.Should().Be(original.RequestMetadata);
        bump.WalletWithdrawalRequestDestinations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Address = DestinationAddress, Amount = 0.01m });

        // RBF: the bump spends exactly the inputs of the request it replaces.
        _coinSelection.Verify(x => x.LockUTXOs(
            It.Is<List<UTXO>>(l => l.Count == 1 && l[0].Outpoint == new OutPoint(_fundingTxId, 0)),
            It.Is<IBitcoinRequest>(r => r.Id == BumpId),
            BitcoinRequestType.WalletWithdrawal), Times.Once);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_ColdWallet_CreatesTheBumpPendingForApprovers()
    {
        SetupOriginal(ColdWallet());

        var bump = await CreateService().CreateBumpRequestAsync(OriginalId, MempoolRecommendedFeesType.CustomFee, 10m);

        bump.Status.Should().Be(WalletWithdrawalRequestStatus.Pending, "cold-wallet bumps must be re-signed by the approvers");
        bump.BumpingWalletWithdrawalRequestId.Should().Be(OriginalId);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_BumpOfABump_KeepsASinglePrefix()
    {
        var original = SetupOriginal(HotWallet());
        original.Description = "Bump of request 7: pay rent";

        var bump = await CreateService().CreateBumpRequestAsync(OriginalId, MempoolRecommendedFeesType.CustomFee, 10m);

        bump.Description.Should().Be($"Bump of request {OriginalId}: pay rent");
    }

    [Fact]
    public async Task CreateBumpRequestAsync_RecommendedFeeType_ResolvesTheRateFromNbxplorer()
    {
        SetupOriginal(HotWallet());
        _nbXplorer.Setup(x => x.GetFeesByType(MempoolRecommendedFeesType.FastestFee, It.IsAny<CancellationToken>()))
            .ReturnsAsync(25m);

        var bump = await CreateService().CreateBumpRequestAsync(OriginalId, MempoolRecommendedFeesType.FastestFee, null);

        bump.MempoolRecommendedFeesType.Should().Be(MempoolRecommendedFeesType.FastestFee);
        bump.CustomFeeRate.Should().Be(25m);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_UnknownRequest_IsRefused()
    {
        _requests.Setup(x => x.GetById(OriginalId)).ReturnsAsync((WalletWithdrawalRequest?)null);

        await AssertRefusedAsync(BumpingErrorReason.RequestNotFound, MempoolRecommendedFeesType.CustomFee, 10m);
    }

    [Theory]
    [InlineData(WalletWithdrawalRequestStatus.Pending)]
    [InlineData(WalletWithdrawalRequestStatus.PSBTSignaturesPending)]
    [InlineData(WalletWithdrawalRequestStatus.OnChainConfirmed)]
    [InlineData(WalletWithdrawalRequestStatus.Bumped)]
    [InlineData(WalletWithdrawalRequestStatus.Failed)]
    public async Task CreateBumpRequestAsync_NotPendingConfirmation_IsRefused(WalletWithdrawalRequestStatus status)
    {
        SetupOriginal(HotWallet(), status: status);

        await AssertRefusedAsync(BumpingErrorReason.InvalidState, MempoolRecommendedFeesType.CustomFee, 10m);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_AlreadyMined_IsRefused()
    {
        SetupOriginal(HotWallet(), confirmations: 1);

        await AssertRefusedAsync(BumpingErrorReason.AlreadyConfirmed, MempoolRecommendedFeesType.CustomFee, 10m);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_TransactionUnknownToNbxplorer_IsRefused()
    {
        SetupOriginal(HotWallet());
        _nbXplorer.Setup(x => x.GetTransactionAsync(It.IsAny<uint256>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TransactionResult?)null);

        await AssertRefusedAsync(BumpingErrorReason.TransactionNotFound, MempoolRecommendedFeesType.CustomFee, 10m);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_ChangelessWithSeveralDestinations_IsRefused()
    {
        SetupOriginal(HotWallet(), destinations: 2, changeless: true);

        await AssertRefusedAsync(BumpingErrorReason.ChangelessMultipleDestinations, MempoolRecommendedFeesType.CustomFee, 10m);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task CreateBumpRequestAsync_FeeRateNotHigherThanTheCurrentOne_IsRefused(int newRate)
    {
        SetupOriginal(HotWallet(), customFeeRate: 2);

        await AssertRefusedAsync(BumpingErrorReason.FeeRateNotHigher, MempoolRecommendedFeesType.CustomFee, newRate);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_CustomFeeWithoutARate_IsRefused()
    {
        SetupOriginal(HotWallet());

        await AssertRefusedAsync(BumpingErrorReason.InvalidFeeRate, MempoolRecommendedFeesType.CustomFee, null);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_FeeExceedingTheInputs_IsRefused()
    {
        // Two destinations of 0.01 BTC funded by exactly 0.02 BTC: no room for a higher fee, let alone the change.
        SetupOriginal(HotWallet(), destinations: 2, inputSats: 2_000_000);

        await AssertRefusedAsync(BumpingErrorReason.FeeExceedsInputs, MempoolRecommendedFeesType.CustomFee, 10m);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_OriginalWithoutLockedUtxos_IsRefusedBeforeAnythingIsCreated()
    {
        SetupOriginal(HotWallet());
        _fmutxos.Setup(x => x.GetLockedUTXOsByWithdrawalId(OriginalId)).ReturnsAsync(new List<FMUTXO>());

        await AssertRefusedAsync(BumpingErrorReason.InvalidState, MempoolRecommendedFeesType.CustomFee, 10m);
    }

    [Fact]
    public async Task CreateBumpRequestAsync_UtxosNoLongerSpendable_CancelsTheHalfCreatedBump()
    {
        SetupOriginal(HotWallet());
        // The inputs left NBXplorer's confirmed UTXO set (e.g. the original got mined in the meantime).
        _coinSelection.Setup(x => x.GetUTXOsByOutpointAsync(It.IsAny<DerivationStrategyBase>(), It.IsAny<List<OutPoint>>()))
            .ReturnsAsync(new List<UTXO>());

        var act = () => CreateService().CreateBumpRequestAsync(OriginalId, MempoolRecommendedFeesType.CustomFee, 10m);

        (await act.Should().ThrowAsync<BumpingException>()).Which.Reason.Should().Be(BumpingErrorReason.InvalidState);
        _storedBump.Should().NotBeNull("the bump row had been inserted before the UTXO lookup");
        _storedBump!.Status.Should().Be(WalletWithdrawalRequestStatus.Cancelled, "a half-prepared bump must not linger as a pending request");
        _requests.Verify(x => x.Update(It.Is<WalletWithdrawalRequest>(r => r.Id == BumpId
            && r.Status == WalletWithdrawalRequestStatus.Cancelled)), Times.Once);
    }

    // ---- CreateCancelRequestAsync --------------------------------------------------------------------------------

    [Fact]
    public async Task CreateCancelRequestAsync_HotWallet_ReturnsTheFundsToTheWallet()
    {
        var original = SetupOriginal(HotWallet());

        var cancel = await CreateService().CreateCancelRequestAsync(OriginalId, MempoolRecommendedFeesType.CustomFee, 10m);

        cancel.Should().BeSameAs(_storedBump);
        cancel.IsRbfCancellation.Should().BeTrue();
        cancel.BumpingWalletWithdrawalRequestId.Should().Be(OriginalId);
        cancel.Status.Should().Be(WalletWithdrawalRequestStatus.PSBTSignaturesPending);
        cancel.Description.Should().Be($"Cancel of request {OriginalId}: pay rent");
        cancel.Changeless.Should().BeTrue("the single output absorbs the fee (GenerateTemplatePSBT's changeless branch)");
        cancel.WithdrawAllFunds.Should().BeFalse();
        cancel.CustomFeeRate.Should().Be(10m);
        cancel.WalletWithdrawalRequestDestinations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Address = ReturnAddress, Amount = 0.1m },
                "everything the locked inputs hold goes back to a fresh address of the wallet");
        cancel.WalletWithdrawalRequestDestinations.Single().Address.Should().NotBe(original.WalletWithdrawalRequestDestinations.Single().Address,
            "the original destination must not be paid");

        _coinSelection.Verify(x => x.LockUTXOs(
            It.Is<List<UTXO>>(l => l.Count == 1 && l[0].Outpoint == new OutPoint(_fundingTxId, 0)),
            It.Is<IBitcoinRequest>(r => r.Id == BumpId),
            BitcoinRequestType.WalletWithdrawal), Times.Once);
    }

    [Fact]
    public async Task CreateCancelRequestAsync_ColdWallet_CreatesTheCancellationPendingForApprovers()
    {
        SetupOriginal(ColdWallet());

        var cancel = await CreateService().CreateCancelRequestAsync(OriginalId, MempoolRecommendedFeesType.CustomFee, 10m);

        cancel.Status.Should().Be(WalletWithdrawalRequestStatus.Pending);
        cancel.IsRbfCancellation.Should().BeTrue();
    }

    [Fact]
    public async Task CreateCancelRequestAsync_ChangelessOriginalWithSeveralDestinations_IsAllowed()
    {
        // A fee bump of this shape is refused (no change to take the fee from); a cancellation replaces every output.
        SetupOriginal(HotWallet(), destinations: 2, changeless: true, inputSats: 2_000_000);

        var cancel = await CreateService().CreateCancelRequestAsync(OriginalId, MempoolRecommendedFeesType.CustomFee, 10m);

        cancel.WalletWithdrawalRequestDestinations.Should().ContainSingle().Which.Amount.Should().Be(0.02m);
    }

    [Fact]
    public async Task CreateCancelRequestAsync_FeeRateNotHigherThanTheCurrentOne_IsRefused()
    {
        SetupOriginal(HotWallet(), customFeeRate: 2);

        await AssertRefusedAsync(BumpingErrorReason.FeeRateNotHigher, MempoolRecommendedFeesType.CustomFee, 2m, cancellation: true);
    }

    [Fact]
    public async Task CreateCancelRequestAsync_AlreadyMined_IsRefused()
    {
        SetupOriginal(HotWallet(), confirmations: 1);

        await AssertRefusedAsync(BumpingErrorReason.AlreadyConfirmed, MempoolRecommendedFeesType.CustomFee, 10m, cancellation: true);
    }

    // ---- ScheduleHotWalletWithdrawalAsync -----------------------------------------------------------------------

    [Fact]
    public async Task ScheduleHotWalletWithdrawalAsync_PendingHotRequest_IsPromotedBeforeTheJobIsScheduled()
    {
        var request = HotRequest(WalletWithdrawalRequestStatus.Pending);
        var events = new List<string>();
        _requests.Setup(x => x.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Callback<WalletWithdrawalRequest>(r => events.Add($"update:{r.Id}:{r.Status}"))
            .Returns((true, (string?)null));
        _bitcoin.Setup(x => x.GenerateTemplatePSBT(It.IsAny<WalletWithdrawalRequest>()))
            .Callback(() => events.Add("template"))
            .ReturnsAsync(EmptyPsbt());
        _scheduler.Setup(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Callback<IJobDetail, ITrigger, CancellationToken>((job, _, _) =>
            {
                job.JobType.Should().Be(typeof(PerformWithdrawalJob));
                job.JobDataMap.GetInt("withdrawalRequestId").Should().Be(BumpId);
                events.Add("schedule");
            })
            .ReturnsAsync(DateTimeOffset.UtcNow);

        var psbt = await CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        psbt.Should().NotBeNull();
        request.Status.Should().Be(WalletWithdrawalRequestStatus.PSBTSignaturesPending);
        events.Should().Equal(new[] { $"update:{BumpId}:PSBTSignaturesPending", "template", "schedule" },
            "PerformWithdrawalJob throws 'Invalid status' unless the request is already PSBTSignaturesPending when it runs");
    }

    [Fact]
    public async Task ScheduleHotWalletWithdrawalAsync_RequestAlreadyPsbtSignaturesPending_DoesNotTouchItsStatus()
    {
        HotRequest(WalletWithdrawalRequestStatus.PSBTSignaturesPending);

        await CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
        _scheduler.Verify(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScheduleHotWalletWithdrawalAsync_Bump_MarksTheReplacedRequestAsBumped()
    {
        var original = SetupOriginal(HotWallet());
        HotRequest(WalletWithdrawalRequestStatus.PSBTSignaturesPending, bumping: OriginalId);

        await CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        original.Status.Should().Be(WalletWithdrawalRequestStatus.Bumped);
        _requests.Verify(x => x.Update(It.Is<WalletWithdrawalRequest>(r => r.Id == OriginalId
            && r.Status == WalletWithdrawalRequestStatus.Bumped)), Times.Once);
    }

    [Fact]
    public async Task ScheduleHotWalletWithdrawalAsync_Cancellation_MarksTheReplacedRequestCancelledWithAReason()
    {
        var original = SetupOriginal(HotWallet());
        HotRequest(WalletWithdrawalRequestStatus.PSBTSignaturesPending, bumping: OriginalId, cancellation: true);

        await CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        original.Status.Should().Be(WalletWithdrawalRequestStatus.Cancelled, "the payment is not happening, it is not merely re-fed");
        original.RejectCancelDescription.Should().Contain("RBF").And.Contain(BumpId.ToString());
    }

    [Fact]
    public async Task ScheduleHotWalletWithdrawalAsync_CancellationSchedulingFails_RestoresTheReplacedRequest()
    {
        var original = SetupOriginal(HotWallet());
        HotRequest(WalletWithdrawalRequestStatus.PSBTSignaturesPending, bumping: OriginalId, cancellation: true);
        _scheduler.Setup(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("quartz down"));

        var act = () => CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        original.RejectCancelDescription.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleHotWalletWithdrawalAsync_SchedulingFails_RevertsTheReplacedRequestAndRethrows()
    {
        var original = SetupOriginal(HotWallet());
        HotRequest(WalletWithdrawalRequestStatus.PSBTSignaturesPending, bumping: OriginalId);
        _scheduler.Setup(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("quartz down"));

        var act = () => CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("quartz down");
        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            "an abandoned bump must give the replaced request back its pending-confirmation status");
    }

    [Fact]
    public async Task ScheduleHotWalletWithdrawalAsync_TemplateGenerationFails_LeavesTheReplacedRequestUntouched()
    {
        var original = SetupOriginal(HotWallet());
        HotRequest(WalletWithdrawalRequestStatus.PSBTSignaturesPending, bumping: OriginalId);
        _bitcoin.Setup(x => x.GenerateTemplatePSBT(It.IsAny<WalletWithdrawalRequest>()))
            .ThrowsAsync(new NoUTXOsAvailableException());

        var act = () => CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        await act.Should().ThrowAsync<NoUTXOsAvailableException>();
        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        _scheduler.Verify(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduleHotWalletWithdrawalAsync_ColdWallet_IsRefused()
    {
        var request = HotRequest(WalletWithdrawalRequestStatus.Pending);
        request.Wallet = ColdWallet();

        var act = () => CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _scheduler.Verify(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(WalletWithdrawalRequestStatus.Cancelled)]
    [InlineData(WalletWithdrawalRequestStatus.Failed)]
    [InlineData(WalletWithdrawalRequestStatus.OnChainConfirmationPending)]
    public async Task ScheduleHotWalletWithdrawalAsync_WrongStatus_IsRefused(WalletWithdrawalRequestStatus status)
    {
        HotRequest(status);

        var act = () => CreateService().ScheduleHotWalletWithdrawalAsync(BumpId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- MarkOriginalAsBumpedAsync / RevertOriginalAsync -------------------------------------------------------

    [Fact]
    public async Task RevertOriginalAsync_OnlyRevertsARequestThatIsBumped()
    {
        var original = SetupOriginal(HotWallet(), status: WalletWithdrawalRequestStatus.OnChainConfirmed);
        var bump = HotRequest(WalletWithdrawalRequestStatus.Cancelled, bumping: OriginalId);

        await CreateService().RevertOriginalAsync(bump);

        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed,
            "a request that was never marked Bumped (or already confirmed) must not be pushed back to pending");
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task RevertOriginalAsync_CancellationByAnotherReplacement_IsLeftAlone()
    {
        var original = SetupOriginal(HotWallet(), status: WalletWithdrawalRequestStatus.Cancelled);
        original.RejectCancelDescription = "Cancelled by RBF replacement request 99";
        var cancel = HotRequest(WalletWithdrawalRequestStatus.Failed, bumping: OriginalId, cancellation: true);

        await CreateService().RevertOriginalAsync(cancel);

        original.Status.Should().Be(WalletWithdrawalRequestStatus.Cancelled);
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task MarkOriginalAsReplacedAsync_NotAReplacement_IsANoOp()
    {
        var request = HotRequest(WalletWithdrawalRequestStatus.PSBTSignaturesPending);

        await CreateService().MarkOriginalAsReplacedAsync(request);

        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    // ---- fixtures ---------------------------------------------------------------------------------------------

    private async Task AssertRefusedAsync(BumpingErrorReason reason, MempoolRecommendedFeesType feeType, decimal? customFeeRate,
        bool cancellation = false)
    {
        var service = CreateService();
        var act = () => cancellation
            ? service.CreateCancelRequestAsync(OriginalId, feeType, customFeeRate)
            : service.CreateBumpRequestAsync(OriginalId, feeType, customFeeRate);

        (await act.Should().ThrowAsync<BumpingException>()).Which.Reason.Should().Be(reason);
        _requests.Verify(x => x.AddAsync(It.IsAny<WalletWithdrawalRequest>()), Times.Never,
            "a refused bump must not create a request");
    }

    private static Wallet HotWallet() => new()
    {
        Id = 3,
        Name = "hot",
        IsHotWallet = true,
        MofN = 1,
        WalletAddressType = WalletAddressType.NativeSegwit,
        Keys = new List<Key> { new() { XPUB = Tpub } },
    };

    private static Wallet ColdWallet()
    {
        var wallet = HotWallet();
        wallet.Id = 2;
        wallet.Name = "cold";
        wallet.IsHotWallet = false;
        return wallet;
    }

    /// <summary>The request being replaced: broadcast, unconfirmed, with its inputs locked.</summary>
    private WalletWithdrawalRequest SetupOriginal(Wallet wallet,
        WalletWithdrawalRequestStatus status = WalletWithdrawalRequestStatus.OnChainConfirmationPending,
        int confirmations = 0, int destinations = 1, bool changeless = false, decimal? customFeeRate = 2,
        long inputSats = 10_000_000)
    {
        var original = new WalletWithdrawalRequest
        {
            Id = OriginalId,
            Status = status,
            TxId = _originalTxId.ToString(),
            Wallet = wallet,
            WalletId = wallet.Id,
            Description = "pay rent",
            Changeless = changeless,
            WithdrawAllFunds = false,
            MempoolRecommendedFeesType = MempoolRecommendedFeesType.CustomFee,
            CustomFeeRate = customFeeRate,
            UserRequestorId = "user-1",
            RequestMetadata = "{\"userName\":\"alice\"}",
            WalletWithdrawalRequestDestinations = Enumerable.Range(0, destinations)
                .Select(_ => new WalletWithdrawalRequestDestination { Address = DestinationAddress, Amount = 0.01m })
                .ToList(),
            UTXOs = new List<FMUTXO> { new() { TxId = _fundingTxId.ToString(), OutputIndex = 0, SatsAmount = inputSats } },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
        };

        _requests.Setup(x => x.GetById(OriginalId)).ReturnsAsync(original);
        _fmutxos.Setup(x => x.GetLockedUTXOsByWithdrawalId(OriginalId)).ReturnsAsync(original.UTXOs);

        var network = Network.RegTest;
        var tx = network.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(_fundingTxId, 0)) { Sequence = Sequence.OptInRBF });
        foreach (var destination in original.WalletWithdrawalRequestDestinations)
        {
            tx.Outputs.Add(new TxOut(new Money(destination.Amount, MoneyUnit.BTC), BitcoinAddress.Create(destination.Address, network)));
        }

        _nbXplorer.Setup(x => x.GetTransactionAsync(_originalTxId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionResult { Confirmations = confirmations, Transaction = tx });

        return original;
    }

    /// <summary>The hot-wallet request to execute (a bump when <paramref name="bumping"/> is set).</summary>
    private WalletWithdrawalRequest HotRequest(WalletWithdrawalRequestStatus status, int? bumping = null, bool cancellation = false)
    {
        var request = new WalletWithdrawalRequest
        {
            Id = BumpId,
            Status = status,
            Wallet = HotWallet(),
            WalletId = 3,
            Description = "pay rent",
            BumpingWalletWithdrawalRequestId = bumping,
            IsRbfCancellation = cancellation,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new() { Address = DestinationAddress, Amount = 0.01m },
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            UTXOs = new List<FMUTXO>(),
        };

        _storedBump = request;
        return request;
    }

    /// <summary>A parseable one-in/one-out PSBT; PSBT.FromTransaction refuses a transaction without inputs.</summary>
    private static PSBT EmptyPsbt()
    {
        var network = Network.RegTest;
        var tx = network.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(new TxOut(Money.Coins(0.01m), BitcoinAddress.Create(DestinationAddress, network)));
        return PSBT.FromTransaction(tx, network);
    }
}
