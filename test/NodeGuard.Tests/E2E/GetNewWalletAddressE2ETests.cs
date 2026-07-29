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
using Grpc.Core;
using Grpc.Net.Client;
using Nodeguard;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// End-to-end coverage for the new <c>derivation_feature</c> field on GetNewWalletAddressRequest,
/// exercised against a LIVE NodeGuard instance (no mocking of NBXplorer/derivation strategy).
/// Gated by <see cref="E2EFactAttribute"/> (RUN_E2E_TESTS=1). Connection via env:
///   NODEGUARD_GRPC_ENDPOINT  default http://localhost:50051 (h2c)
///   NODEGUARD_API_TOKEN      default the dev "Liquidator" token
///   E2E_HOT_WALLET_ID        NodeGuard hot wallet to query (default 3)
/// </summary>
[Trait("Category", "E2E")]
public class GetNewWalletAddressE2ETests
{
    private const string DefaultDevToken = "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=";

    private readonly ITestOutputHelper _output;

    public GetNewWalletAddressE2ETests(ITestOutputHelper output)
    {
        _output = output;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    [E2EFact]
    public async Task GetNewWalletAddress_DefaultsToDeposit_WhenFeatureOmitted()
    {
        var client = CreateClient(out var headers);
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        var withoutFeature = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest { WalletId = walletId, Skip = 0, Reserve = false }, headers);

        var withDeposit = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest
            {
                WalletId = walletId, Skip = 0, Reserve = false,
                DerivationFeature = DERIVATION_FEATURE.Deposit
            }, headers);

        _output.WriteLine($"omitted={withoutFeature.Address} deposit={withDeposit.Address}");
        withoutFeature.Address.Should().Be(withDeposit.Address,
            "omitting derivation_feature should default to DEPOSIT");
    }

    [E2ETheory]
    [InlineData(DERIVATION_FEATURE.Deposit)]
    [InlineData(DERIVATION_FEATURE.Change)]
    [InlineData(DERIVATION_FEATURE.Direct)]
    [InlineData(DERIVATION_FEATURE.Custom)]
    public async Task GetNewWalletAddress_Reserve_DoesNotReturnSameAddressTwice(DERIVATION_FEATURE feature)
    {
        var client = CreateClient(out var headers);
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        var first = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest
            {
                WalletId = walletId, Skip = 0, Reserve = true,
                DerivationFeature = feature
            }, headers);

        var second = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest
            {
                WalletId = walletId, Skip = 0, Reserve = true,
                DerivationFeature = feature
            }, headers);

        _output.WriteLine($"feature={feature} first={first.Address} second={second.Address}");
        second.Address.Should().NotBe(first.Address,
            $"reserving a {feature} address should mark it used so the next call returns a fresh one");
    }

    [E2ETheory]
    [InlineData(DERIVATION_FEATURE.Deposit, DERIVATION_FEATURE.Change)]
    [InlineData(DERIVATION_FEATURE.Deposit, DERIVATION_FEATURE.Direct)]
    [InlineData(DERIVATION_FEATURE.Deposit, DERIVATION_FEATURE.Custom)]
    [InlineData(DERIVATION_FEATURE.Change, DERIVATION_FEATURE.Direct)]
    [InlineData(DERIVATION_FEATURE.Change, DERIVATION_FEATURE.Custom)]
    [InlineData(DERIVATION_FEATURE.Direct, DERIVATION_FEATURE.Custom)]
    public async Task GetNewWalletAddress_DifferentFeatures_ReturnDifferentAddresses(
        DERIVATION_FEATURE featureA, DERIVATION_FEATURE featureB)
    {
        var client = CreateClient(out var headers);
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        var addressA = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest
            {
                WalletId = walletId, Skip = 0, Reserve = false,
                DerivationFeature = featureA
            }, headers);

        var addressB = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest
            {
                WalletId = walletId, Skip = 0, Reserve = false,
                DerivationFeature = featureB
            }, headers);

        _output.WriteLine($"{featureA}={addressA.Address} {featureB}={addressB.Address}");
        addressA.Address.Should().NotBeNullOrEmpty();
        addressB.Address.Should().NotBeNullOrEmpty();
        addressB.Address.Should().NotBe(addressA.Address,
            $"{featureA} and {featureB} derivation branches must produce different addresses");
    }

    private static NodeGuardService.NodeGuardServiceClient CreateClient(out Metadata headers)
    {
        var endpoint = Env("NODEGUARD_GRPC_ENDPOINT", "http://localhost:50051");
        headers = new Metadata { { "auth-token", Env("NODEGUARD_API_TOKEN", DefaultDevToken) } };
        return new NodeGuardService.NodeGuardServiceClient(GrpcChannel.ForAddress(endpoint));
    }

    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
}
