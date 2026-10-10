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
using Microsoft.Extensions.Logging.Abstractions;
using NSpark;
using NSpark.Signer;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NodeGuard.Services.Spark;

public class SparkWalletServiceTests
{
    private static readonly SparkSettings Mainnet = new()
    {
        Enabled = true,
        Network = SparkNetwork.Mainnet,
        Operators = SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet),
        SignerMode = SparkSignerMode.Remote,
        SeedFingerprint = "ed0210c8",
        IdentityPublicKey = SparkSettingsTests.Identity
    };

    private readonly ISparkSignerProvider _signers = Substitute.For<ISparkSignerProvider>();
    private readonly ManualTime _time = new();

    private SparkWalletService Service(SparkSettings settings) =>
        new(settings, _signers, NullLogger<SparkWalletService>.Instance, NullLoggerFactory.Instance, _time);

    [Fact]
    public async Task WhenDisabled_SparkReportsItAndRefusesOperations()
    {
        var service = Service(SparkSettings.Disabled);

        (await service.EnsureReadyAsync()).State.Should().Be(SparkWalletState.Disabled);
        await service.Invoking(s => s.CreateInvoiceAsync(1000, "swap", 3600)).Should()
            .ThrowAsync<SparkUnavailableException>().WithMessage("*SPARK_ENABLED*");
        await _signers.DidNotReceiveWithAnyArgs().GetSignerAsync(default!, default);
    }

    [Fact]
    public async Task ARefusedSigner_MakesSparkUnavailableWithTheReason_AndIsRetriedAfterAMinute()
    {
        _signers.GetSignerAsync(Arg.Any<SparkOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkUnavailableException("the signer's Spark identity is wrong"));
        var service = Service(Mainnet);

        var status = await service.EnsureReadyAsync();

        status.State.Should().Be(SparkWalletState.Unavailable);
        status.Reason.Should().Be("the signer's Spark identity is wrong");
        await service.Invoking(s => s.GetBalanceAsync()).Should()
            .ThrowAsync<SparkUnavailableException>().WithMessage("the signer's Spark identity is wrong");
        await _signers.ReceivedWithAnyArgs(1).GetSignerAsync(default!, default);

        _time.Advance(TimeSpan.FromMinutes(1));
        await service.EnsureReadyAsync();

        await _signers.ReceivedWithAnyArgs(2).GetSignerAsync(default!, default);
    }

    [Fact]
    public async Task TheSignerIsAskedWithNodeGuardsOperatorsAndSsp()
    {
        SparkOptions? asked = null;
        _signers.GetSignerAsync(Arg.Do<SparkOptions>(o => asked = o), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkUnavailableException("stop here"));

        await Service(Mainnet).EnsureReadyAsync();

        asked!.SigningOperators.Should().BeEquivalentTo(SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet));
        asked.EffectiveSspIdentityPublicKeyHex.Should().Be(SparkOptions.GetSspIdentityPublicKey(SparkNetwork.Mainnet));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
