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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NSubstitute;

namespace NodeGuard.Services.Spark;

public class SparkSignerIdentityPinTests
{
    private const string Fingerprint = "ed0210c8";
    private const string Identity = SparkSettingsTests.Identity;
    private const string Other = SparkSettingsTests.SspIdentity;

    private readonly InMemorySparkSignerIdentities _identities = new();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    private SparkSignerIdentityPin Pin(string? configured = null, int account = 0, SparkSignerMode mode = SparkSignerMode.Remote)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISparkSignerIdentityRepository>(_identities)
            .AddSingleton(_audit)
            .BuildServiceProvider();
        var settings = new SparkSettings
        {
            Enabled = true,
            SignerMode = mode,
            SeedFingerprint = Fingerprint,
            Account = account,
            IdentityPublicKey = configured
        };
        return new SparkSignerIdentityPin(settings, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SparkSignerIdentityPin>.Instance);
    }

    [Fact]
    public async Task BeforeTheFirstHandshake_NoIdentityIsExpected() =>
        (await Pin().ExpectedAsync()).Should().BeNull();

    [Fact]
    public async Task TheFirstHandshake_SavesTheIdentityTheSignerServes_AndAuditsIt()
    {
        var pin = Pin();

        await pin.PinOrVerifyAsync(Identity.ToUpperInvariant());

        _identities.Rows.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { SeedFingerprint = Fingerprint, Account = 0, IdentityPublicKey = Identity });
        (await pin.ExpectedAsync()).Should().Be(Identity);
        await _audit.Received(1).LogSystemAsync(AuditActionType.Create, AuditEventType.Success, AuditObjectType.Wallet,
            $"spark-signer/{Fingerprint}/0", Arg.Any<object?>());
    }

    [Fact]
    public async Task ALaterHandshake_ServingTheSavedIdentity_Passes_AndSavesNothingNew()
    {
        _identities.Saved(Fingerprint, 0, Identity);
        var pin = Pin();

        await pin.PinOrVerifyAsync(Identity);

        _identities.Rows.Should().ContainSingle();
        await _audit.DidNotReceiveWithAnyArgs().LogSystemAsync(default, default, default);
    }

    [Fact]
    public async Task ALaterHandshake_ServingAnotherIdentity_IsRefused()
    {
        _identities.Saved(Fingerprint, 0, Identity);

        var act = () => Pin().PinOrVerifyAsync(Other);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should()
            .Contain(Other).And.Contain(Identity).And.Contain("docs/spark-swaps.md");
        _identities.Rows.Should().ContainSingle().Which.IdentityPublicKey.Should().Be(Identity);
    }

    [Fact]
    public async Task TheSavedIdentity_IsPerSeedAndAccount()
    {
        _identities.Saved(Fingerprint, 0, Identity);
        var pin = Pin(account: 1);

        await pin.PinOrVerifyAsync(Other);

        (await pin.ExpectedAsync()).Should().Be(Other);
        _identities.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task AConfiguredIdentity_IsExpectedBeforeAnyHandshake_AndSavedOnTheFirst()
    {
        var pin = Pin(configured: Identity);

        (await pin.ExpectedAsync()).Should().Be(Identity);
        await pin.PinOrVerifyAsync(Identity);

        _identities.Rows.Should().ContainSingle().Which.IdentityPublicKey.Should().Be(Identity);
    }

    [Fact]
    public async Task ASignerServingAnotherIdentityThanTheConfiguredOne_IsRefused_AndNothingIsSaved()
    {
        var act = () => Pin(configured: Identity).PinOrVerifyAsync(Other);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should().Contain("SPARK_IDENTITY_PUBKEY");
        _identities.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task AConfiguredIdentityThatDisagreesWithTheSavedOne_IsRefused()
    {
        _identities.Saved(Fingerprint, 0, Identity);

        var act = () => Pin(configured: Other).PinOrVerifyAsync(Other);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should()
            .Contain("SPARK_IDENTITY_PUBKEY is " + Other).And.Contain("saved " + Identity);
    }

    [Fact]
    public async Task AnotherNodeGuardSavingFirst_WinsTheRace()
    {
        _identities.SavedConcurrently = Identity;

        var act = () => Pin().PinOrVerifyAsync(Other);

        await act.Should().ThrowAsync<SparkUnavailableException>();
        _identities.Rows.Should().ContainSingle().Which.IdentityPublicKey.Should().Be(Identity);
        await _audit.DidNotReceiveWithAnyArgs().LogSystemAsync(default, default, default);
    }

    [Fact]
    public async Task TheSavedIdentity_IsReadOnce()
    {
        _identities.Saved(Fingerprint, 0, Identity);
        var pin = Pin();

        await pin.ExpectedAsync();
        await pin.PinOrVerifyAsync(Identity);
        await pin.ExpectedAsync();

        _identities.Reads.Should().Be(1);
    }

    [Fact]
    public async Task AFailedAudit_DoesNotUndoTheSavedIdentity()
    {
        _audit.LogSystemAsync(default, default, default).ReturnsForAnyArgs<Task>(_ => throw new InvalidOperationException("audit down"));
        var pin = Pin();

        await pin.PinOrVerifyAsync(Identity);

        (await pin.ExpectedAsync()).Should().Be(Identity);
    }

    [Fact]
    public async Task InWalletMode_NothingIsExpected() =>
        (await Pin(configured: Identity, mode: SparkSignerMode.Wallet).ExpectedAsync()).Should().BeNull();
}
