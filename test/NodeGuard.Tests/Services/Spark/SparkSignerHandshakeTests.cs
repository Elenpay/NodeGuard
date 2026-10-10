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
using NSpark;
using NSpark.RemoteSigner;

namespace NodeGuard.Services.Spark;

public class SparkSignerHandshakeTests
{
    private static readonly SparkOptions Options = new()
    {
        Network = SparkNetwork.Mainnet,
        SigningOperators = SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet)
    };

    private static readonly string Ssp = SparkOptions.GetSspIdentityPublicKey(SparkNetwork.Mainnet);

    private static SparkSignerInfo Info(int version = SparkSignerContract.Version, string identity = SparkSettingsTests.Identity,
        IReadOnlyList<string>? operators = null, uint threshold = 2, IReadOnlyList<string>? receivers = null) =>
        new(version, identity,
            operators ?? Options.SigningOperators.Select(o => o.IdentityPublicKeyHex.ToUpperInvariant()).ToList(),
            threshold, receivers ?? [Ssp]);

    [Fact]
    public void ASignerConfiguredLikeNodeGuard_Passes() =>
        SparkSignerHandshake.Verify(Info(), SparkSettingsTests.Identity, Options).Should().BeEmpty();

    [Fact]
    public void AnotherContractVersion_IsReported() =>
        SparkSignerHandshake.Verify(Info(version: SparkSignerContract.Version + 1), SparkSettingsTests.Identity, Options)
            .Should().ContainSingle().Which.Should().Contain("contract version");

    [Fact]
    public void AnotherIdentity_IsReported() =>
        SparkSignerHandshake.Verify(Info(identity: Ssp), SparkSettingsTests.Identity, Options)
            .Should().ContainSingle().Which.Should().Contain("SPARK_IDENTITY_PUBKEY");

    [Fact]
    public void AnotherOperatorSet_IsReported() =>
        SparkSignerHandshake.Verify(Info(operators: Options.SigningOperators.Take(2).Select(o => o.IdentityPublicKeyHex).ToList()),
                SparkSettingsTests.Identity, Options)
            .Should().ContainSingle().Which.Should().Contain("SPARK_OPERATOR_KEYS");

    [Fact]
    public void AnotherThreshold_IsReported() =>
        SparkSignerHandshake.Verify(Info(threshold: 3), SparkSettingsTests.Identity, Options)
            .Should().ContainSingle().Which.Should().Contain("SPARK_THRESHOLD");

    [Fact]
    public void AReceiverListWithoutTheSsp_IsReported() =>
        SparkSignerHandshake.Verify(Info(receivers: []), SparkSettingsTests.Identity, Options)
            .Should().ContainSingle().Which.Should().Contain("SPARK_ALLOWED_RECEIVERS");
}
