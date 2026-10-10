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

using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NSpark;
using NSpark.RemoteSigner;
using NSpark.Signer;
using NSubstitute;

namespace NodeGuard.Services.Spark;

public class SparkSignerProviderTests
{
    /// <summary>The public dev test vector (master fingerprint ed0210c8); never fund it.</summary>
    internal const string DevMnemonic =
        "middle teach digital prefer fiscal theory syrup enter crash muffin easily anxiety ill barely eagle swim volume consider dynamic unaware deputy middle into physical";

    internal static readonly IReadOnlyList<SigningOperatorConfig> Operators =
    [
        new("https://spark-operator-0:8535", "0000000000000000000000000000000000000000000000000000000000000001",
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"),
        new("https://spark-operator-1:8535", "0000000000000000000000000000000000000000000000000000000000000002",
            "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5"),
        new("https://spark-operator-2:8535", "0000000000000000000000000000000000000000000000000000000000000003",
            "02f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9")
    ];

    private static SparkSettings Settings(SparkSignerMode mode, string? identity = SparkSettingsTests.Identity,
        string? fingerprint = "ed0210c8") => new()
    {
        Enabled = true,
        Network = SparkNetwork.Regtest,
        Operators = Operators,
        SspUrl = "http://spark-ssp:5000/graphql/spark/2025-03-19",
        SspIdentityPublicKey = SparkSettingsTests.SspIdentity,
        SignerMode = mode,
        SeedFingerprint = fingerprint,
        Account = 0,
        IdentityPublicKey = identity,
        SignerEndpoint = new Uri("https://abc.lambda-url.eu-central-1.on.aws/"),
        SignerRieUrl = new Uri("http://spark-signer:8080/2015-03-31/functions/function/invocations")
    };

    private static SparkOptions Options(SparkSettings settings) => settings.ToSparkOptions(null);

    private static readonly ISparkSeedProtector Protector = new SparkSeedProtector(new EphemeralDataProtectionProvider());

    private static SparkSignerProvider Provider(SparkSettings settings, HttpMessageHandler? signer = null,
        IWalletRepository? wallets = null, ISparkSeedProtector? protector = null)
    {
        var services = new ServiceCollection();
        if (wallets is not null) services.AddSingleton(wallets);
        return new SparkSignerProvider(settings, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            protector ?? Protector, new HttpClient(signer ?? new SignerUnderEmulator(SparkSignerPolicy.Create([], 0, []))),
            () => new ImmutableCredentials("AKIDEXAMPLE", "not-a-real-secret", null), "eu-central-1");
    }

    private static SparkSignerPolicy MatchingPolicy() =>
        SparkSignerPolicy.Create(Operators.Select(o => o.IdentityPublicKeyHex), 2, [SparkSettingsTests.SspIdentity]);

    /// <summary>Wallet 5: a Spark wallet with the dev mnemonic, encrypted by <paramref name="protector"/>.</summary>
    private static IWalletRepository SparkWalletRow(string? identity = SparkSettingsTests.Identity, ISparkSeedProtector? protector = null,
        WalletKind kind = WalletKind.Spark)
    {
        var wallets = Substitute.For<IWalletRepository>();
        wallets.GetById(5).Returns(new Wallet
        {
            Id = 5,
            Name = "transit",
            Kind = kind,
            SparkEncryptedMnemonic = (protector ?? Protector).Protect(DevMnemonic),
            SparkAccount = 0,
            SparkIdentityPublicKey = identity
        });
        return wallets;
    }

    [Fact]
    public async Task TheRemoteSigner_IsUsedAfterAMatchingHandshake()
    {
        var provider = Provider(Settings(SparkSignerMode.Remote), new SignerUnderEmulator(MatchingPolicy()));

        var signer = await provider.GetSignerAsync(SparkWalletRef.RemoteSigner, Options(Settings(SparkSignerMode.Remote)), CancellationToken.None);

        signer.Should().BeOfType<RemoteSparkSigner>();
        Convert.ToHexString(await signer.GetIdentityPublicKeyAsync()).ToLowerInvariant().Should().Be(SparkSettingsTests.Identity);
    }

    [Fact]
    public async Task ARemoteSignerWithAnotherPolicy_IsRefused()
    {
        var settings = Settings(SparkSignerMode.Remote);
        var provider = Provider(settings, new SignerUnderEmulator(SparkSignerPolicy.Create([], 0, [])));

        var act = () => provider.GetSignerAsync(SparkWalletRef.RemoteSigner, Options(settings), CancellationToken.None);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should()
            .Contain("SPARK_OPERATOR_KEYS").And.Contain("SPARK_THRESHOLD").And.Contain("SPARK_ALLOWED_RECEIVERS");
    }

    [Fact]
    public async Task ARemoteSignerServingAnotherIdentity_IsRefused()
    {
        var settings = Settings(SparkSignerMode.Remote, identity: SparkSettingsTests.SspIdentity);
        var provider = Provider(settings, new SignerUnderEmulator(MatchingPolicy()));

        var act = () => provider.GetSignerAsync(SparkWalletRef.RemoteSigner, Options(settings), CancellationToken.None);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should().Contain("SPARK_IDENTITY_PUBKEY");
    }

    [Fact]
    public async Task ASignerWithoutSparkRoutes_IsReportedAsSuch()
    {
        // A signer that predates the Spark routes answers every path as the PSBT signer
        var settings = Settings(SparkSignerMode.Remote);
        var provider = Provider(settings, new SparkSignerTransportTests.FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"statusCode":500,"body":"Object reference not set to an instance of an object."}""")
        }));

        var act = () => provider.GetSignerAsync(SparkWalletRef.RemoteSigner, Options(settings), CancellationToken.None);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should().Contain("not a Spark signer envelope");
    }

    [Fact]
    public async Task ASparkWallet_DerivesItsKeysFromItsDecryptedMnemonic()
    {
        var settings = Settings(SparkSignerMode.Wallet);

        var signer = await Provider(settings, wallets: SparkWalletRow())
            .GetSignerAsync(new SparkWalletRef(5), Options(settings), CancellationToken.None);

        (await signer.GetIdentityPublicKeyAsync()).Should()
            .Equal(await SparkSigner.FromMnemonic(DevMnemonic, 0).GetIdentityPublicKeyAsync());
    }

    [Fact]
    public async Task ASparkWallet_WhoseSeedDerivesAnotherIdentity_IsUnavailable()
    {
        var settings = Settings(SparkSignerMode.Wallet);

        var act = () => Provider(settings, wallets: SparkWalletRow(identity: SparkSettingsTests.SspIdentity))
            .GetSignerAsync(new SparkWalletRef(5), Options(settings), CancellationToken.None);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should().Contain("recorded at creation");
    }

    [Fact]
    public async Task ASparkWallet_ThatNoLongerDecrypts_IsUnavailable()
    {
        // Encrypted under another key ring: a lost DataProtectionKeys table, or another application name
        var settings = Settings(SparkSignerMode.Wallet);
        var otherKeyRing = new SparkSeedProtector(new EphemeralDataProtectionProvider());

        var act = () => Provider(settings, wallets: SparkWalletRow(protector: otherKeyRing))
            .GetSignerAsync(new SparkWalletRef(5), Options(settings), CancellationToken.None);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should().Contain("cannot decrypt its seed");
    }

    [Fact]
    public async Task AnOnChainWallet_HasNoSparkSigner()
    {
        var settings = Settings(SparkSignerMode.Wallet);

        var act = () => Provider(settings, wallets: SparkWalletRow(kind: WalletKind.OnChain))
            .GetSignerAsync(new SparkWalletRef(5), Options(settings), CancellationToken.None);

        (await act.Should().ThrowAsync<SparkUnavailableException>()).Which.Message.Should().Contain("not a Spark wallet");
    }

    [Fact]
    public async Task AWalletOfTheOtherSignerMode_IsRefused()
    {
        var remote = Settings(SparkSignerMode.Remote);
        var wallet = Settings(SparkSignerMode.Wallet);

        await Provider(remote).Invoking(p => p.GetSignerAsync(new SparkWalletRef(5), Options(remote), CancellationToken.None))
            .Should().ThrowAsync<SparkUnavailableException>().WithMessage("SPARK_SIGNER=remote*");
        await Provider(wallet).Invoking(p => p.GetSignerAsync(SparkWalletRef.RemoteSigner, Options(wallet), CancellationToken.None))
            .Should().ThrowAsync<SparkUnavailableException>().WithMessage("SPARK_SIGNER=wallet*");
    }

    /// <summary>
    /// The remote signer's Spark routes (NSpark.RemoteSigner) behind the Lambda runtime interface
    /// emulator, with the dev seed in process.
    /// </summary>
    private sealed class SignerUnderEmulator(SparkSignerPolicy policy) : HttpMessageHandler
    {
        private readonly ISparkSigner _seed = SparkSigner.FromMnemonic(DevMnemonic, 0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var invocation = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var (status, body) = await SparkSignerHttpHandler.HandleAsync(
                invocation.RootElement.GetProperty("rawPath").GetString(),
                invocation.RootElement.GetProperty("body").GetString(),
                (wallet, _) => wallet == "ed0210c8"
                    ? Task.FromResult(_seed)
                    : Task.FromException<ISparkSigner>(new SparkSignerPolicyException("unknown wallet")),
                policy, ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { statusCode = status, body }), Encoding.UTF8, "application/json")
            };
        }
    }
}
