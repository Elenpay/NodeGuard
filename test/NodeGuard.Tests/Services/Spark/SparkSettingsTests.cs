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
using NBitcoin;
using NSpark;

namespace NodeGuard.Services.Spark;

public class SparkSettingsTests
{
    internal const string Identity = "032986ec06431b7b80a4171836174ac10c07e1cb1dff4af93082ee5f3ebca64a0a";
    internal const string SspIdentity = "022bf283544b16c0622daecb79422007d167eca6ce9f0c98c0c49833b1f7170bfe";

    internal const string RegtestOperators =
        "https://spark-operator-0:8535|0000000000000000000000000000000000000000000000000000000000000001|0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798;" +
        "https://spark-operator-1:8535|0000000000000000000000000000000000000000000000000000000000000002|02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5;" +
        "https://spark-operator-2:8535|0000000000000000000000000000000000000000000000000000000000000003|02f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9";

    private static Dictionary<string, string?> Remote() => new()
    {
        ["SPARK_ENABLED"] = "true",
        ["SPARK_SEED_FINGERPRINT"] = "ED0210C8",
        ["SPARK_ACCOUNT"] = "1",
        ["SPARK_IDENTITY_PUBKEY"] = Identity.ToUpperInvariant()
    };

    private static Dictionary<string, string?> Regtest() => new(Remote())
    {
        ["SPARK_OPERATORS"] = RegtestOperators,
        ["SPARK_SSP_URL"] = "http://spark-ssp:5000/graphql/spark/2025-03-19"
    };

    private static SparkSettings Read(Dictionary<string, string?> env, Network? network = null, bool dev = false,
        bool remote = true, string? endpoint = "https://abc.lambda-url.eu-central-1.on.aws/") =>
        SparkSettings.FromEnvironment(name => env.GetValueOrDefault(name), network ?? Network.Main, dev, remote, endpoint);

    private static string Error(Dictionary<string, string?> env, Network? network = null, bool dev = false, bool remote = true,
        string? endpoint = "https://abc.lambda-url.eu-central-1.on.aws/")
    {
        var act = () => Read(env, network, dev, remote, endpoint);
        return act.Should().Throw<InvalidOperationException>().Which.Message;
    }

    [Fact]
    public void WithoutSparkEnabled_SparkIsDisabled()
    {
        Read(new Dictionary<string, string?>()).Enabled.Should().BeFalse();
        Read(new Dictionary<string, string?> { ["SPARK_ENABLED"] = "false", ["SPARK_ACCOUNT"] = "x" }).Enabled.Should().BeFalse();
    }

    [Fact]
    public void OnMainnet_LightsparksOperatorsAndSspAreTheDefaults()
    {
        var settings = Read(Remote());

        settings.Enabled.Should().BeTrue();
        settings.Network.Should().Be(SparkNetwork.Mainnet);
        settings.Operators.Should().BeEquivalentTo(SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet));
        settings.SspUrl.Should().Be(SparkOptions.DefaultSspUrl);
        settings.SspIdentityPublicKey.Should().BeNull();
        settings.SignerMode.Should().Be(SparkSignerMode.Remote);
        settings.SeedFingerprint.Should().Be("ed0210c8");
        settings.Account.Should().Be(1);
        settings.IdentityPublicKey.Should().Be(Identity);
        settings.MaxExitFeeSats.Should().Be(SparkSettings.DefaultMaxExitFeeSats);
        settings.ExitIntervalMinutes.Should().BeNull("the exit job then runs every 10 minutes, 1 in a dev environment");
        settings.SignerEndpoint.Should().Be(new Uri("https://abc.lambda-url.eu-central-1.on.aws/"));

        var options = settings.ToSparkOptions(null);
        options.EffectiveSigningThreshold.Should().Be(2);
        options.EffectiveSspIdentityPublicKeyHex.Should().Be(SparkOptions.GetSspIdentityPublicKey(SparkNetwork.Mainnet));
    }

    [Fact]
    public void OffMainnet_OperatorsAndSspMustBeConfigured()
    {
        var message = Error(Remote(), Network.RegTest);

        message.Should().Contain("SPARK_OPERATORS is required").And.Contain("SPARK_SSP_URL is required");
    }

    [Fact]
    public void OnRegtest_TheConfiguredNetworkIsRead()
    {
        var env = Regtest();
        env["SPARK_THRESHOLD"] = "2";
        env["SPARK_OPERATOR_CERTS_DIR"] = "/certs";
        env["SPARK_SIGNER_RIE_URL"] = "http://spark-signer:8080/2015-03-31/functions/function/invocations";
        env["SPARK_MAX_EXIT_FEE_SATS"] = "5000";
        env["SPARK_EXIT_INTERVAL_MINUTES"] = "3";

        var settings = Read(env, Network.RegTest);

        settings.Network.Should().Be(SparkNetwork.Regtest);
        settings.Operators.Should().HaveCount(3);
        settings.Operators[1].Address.Should().Be("https://spark-operator-1:8535");
        settings.Threshold.Should().Be(2u);
        settings.OperatorCertsDirectory.Should().Be("/certs");
        settings.SignerRieUrl!.Port.Should().Be(8080);
        settings.MaxExitFeeSats.Should().Be(5000);
        settings.ExitIntervalMinutes.Should().Be(3);
        settings.ToSparkOptions(SspIdentity).EffectiveSspIdentityPublicKeyHex.Should().Be(SspIdentity);
    }

    [Theory]
    [InlineData("SPARK_OPERATOR_CERTS_DIR", "/certs", "not allowed on mainnet")]
    [InlineData("SPARK_SIGNER_RIE_URL", "http://localhost:9000/", "not allowed on mainnet")]
    [InlineData("SPARK_SSP_URL", "https://ssp.example.com/graphql", "SPARK_SSP_IDENTITY_PUBKEY is required")]
    [InlineData("SPARK_OPERATORS", "https://op|01|02", "SPARK_OPERATORS entries")]
    [InlineData("SPARK_THRESHOLD", "4", "SPARK_THRESHOLD")]
    [InlineData("SPARK_THRESHOLD", "0", "SPARK_THRESHOLD")]
    [InlineData("SPARK_SSP_IDENTITY_PUBKEY", "04abcd", "SPARK_SSP_IDENTITY_PUBKEY must be")]
    [InlineData("SPARK_IDENTITY_PUBKEY", "zz", "SPARK_IDENTITY_PUBKEY must be")]
    [InlineData("SPARK_SEED_FINGERPRINT", "not-a-fingerprint", "SPARK_SEED_FINGERPRINT must be")]
    [InlineData("SPARK_ACCOUNT", "-1", "SPARK_ACCOUNT must be")]
    [InlineData("SPARK_ACCOUNT", "one", "SPARK_ACCOUNT must be")]
    [InlineData("SPARK_MAX_EXIT_FEE_SATS", "0", "SPARK_MAX_EXIT_FEE_SATS")]
    [InlineData("SPARK_EXIT_INTERVAL_MINUTES", "0", "SPARK_EXIT_INTERVAL_MINUTES")]
    [InlineData("SPARK_EXIT_INTERVAL_MINUTES", "ten", "SPARK_EXIT_INTERVAL_MINUTES")]
    public void InvalidSettings_FailTheStartup(string variable, string? value, string expected)
    {
        var env = Remote();
        env[variable] = value;

        Error(env).Should().Contain(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutASparkAccount_AccountZeroIsUsed(string? account)
    {
        var env = Remote();
        env["SPARK_ACCOUNT"] = account;

        Read(env).Account.Should().Be(0);
    }

    [Fact]
    public void TheRemoteSigner_NeedsTheSeedFingerprintAndTheIdentity()
    {
        var env = Remote();
        env.Remove("SPARK_SEED_FINGERPRINT");
        env.Remove("SPARK_IDENTITY_PUBKEY");

        Error(env).Should().Contain("SPARK_SEED_FINGERPRINT is required").And.Contain("SPARK_IDENTITY_PUBKEY is required");
    }

    [Theory]
    [InlineData("https://abc.lambda-url.eu-central-1.on.aws/sign")]
    [InlineData("https://abc.lambda-url.eu-central-1.on.aws/?x=1")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void TheRemoteSignerEndpoint_MustHaveNoPath(string? endpoint) =>
        Error(Remote(), endpoint: endpoint).Should().Contain("REMOTE_SIGNER_ENDPOINT");

    [Fact]
    public void AnEndpointWithoutTrailingSlash_IsTheRoot() =>
        Read(Remote(), endpoint: "https://abc.lambda-url.eu-central-1.on.aws").SignerEndpoint!.AbsolutePath.Should().Be("/");

    [Fact]
    public void TheEmbeddedSigner_InADevEnvironment_DefaultsToTheCurrentInternalWallet()
    {
        var env = Regtest();
        env.Remove("SPARK_SEED_FINGERPRINT");
        env.Remove("SPARK_IDENTITY_PUBKEY");

        var settings = Read(env, Network.RegTest, dev: true, remote: false, endpoint: null);

        settings.SignerMode.Should().Be(SparkSignerMode.Embedded);
        settings.SeedFingerprint.Should().BeNull();
        settings.IdentityPublicKey.Should().BeNull();
    }

    [Fact]
    public void TheEmbeddedSigner_OutsideADevEnvironment_NeedsTheSeedFingerprint()
    {
        var env = Remote();
        env.Remove("SPARK_SEED_FINGERPRINT");

        Error(env, remote: false, endpoint: null).Should().Contain("SPARK_SEED_FINGERPRINT is required");
    }

    [Fact]
    public void SparkRunsOnlyOnMainnetAndRegtest() =>
        Error(Remote(), Network.TestNet).Should().Contain("mainnet or regtest");
}
