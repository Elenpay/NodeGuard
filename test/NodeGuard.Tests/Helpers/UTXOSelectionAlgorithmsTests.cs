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
using NodeGuard.Helpers;
using NodeGuard.TestHelpers;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.Models;

namespace NodeGuard.Tests
{
    public class UTXOSelectionAlgorithmsTests
    {
        /// <summary>
        /// The amount requested in every test, so each UTXO amount below can be read as "this much
        /// above/below what we are paying"
        /// </summary>
        private const long RequestedSats = 50_000;

        private readonly Wallet _wallet = CreateWallet.SingleSig(CreateWallet.CreateInternalWallet());
        private readonly ILogger _logger = new Mock<ILogger>().Object;

        /// <summary>
        /// A UTXO holding the given amount. The outpoint is derived from the amount so that each UTXO is
        /// identified by the only thing these algorithms look at: how much it holds
        /// </summary>
        private static UTXO UtxoOf(long satoshis)
        {
            return new UTXO
            {
                Outpoint = new OutPoint(new uint256((ulong)satoshis), 0),
                Value = new Money(satoshis)
            };
        }

        private List<UTXO> SelectByClosest(params UTXO[] availableUTXOs)
        {
            return UTXOSelectionAlgorithms.SelectUTXOsByClosest(
                _wallet, RequestedSats, availableUTXOs.ToList(), _logger);
        }

        [Fact]
        public void SelectUTXOsByClosest_WithAnExactMatch_SelectsOnlyTheExactMatch()
        {
            // Arrange: distances to the 50_000 requested are 40_000 | 0 | 70_000
            var tooSmall = UtxoOf(10_000);
            var exactMatch = UtxoOf(50_000);
            var tooBig = UtxoOf(120_000);

            // Act
            var selectedUTXOs = SelectByClosest(tooSmall, exactMatch, tooBig);

            // Assert: the exact match is the closest one and covers the amount by itself
            selectedUTXOs.Should().Equal(exactMatch);
        }

        [Fact]
        public void SelectUTXOsByClosest_WhenTheClosestUTXOIsBiggerThanTheAmount_SelectsOnlyThatUTXO()
        {
            // Arrange: distances to the 50_000 requested are 5_000 | 20_000 | 30_000, so the only UTXO
            // above the amount is also the closest one
            var closestAndAboveTheAmount = UtxoOf(55_000);
            var belowTheAmount = UtxoOf(30_000);
            var furtherBelowTheAmount = UtxoOf(20_000);

            // Act
            var selectedUTXOs = SelectByClosest(closestAndAboveTheAmount, belowTheAmount, furtherBelowTheAmount);

            // Assert: it covers the amount on its own, so no other UTXO is needed
            selectedUTXOs.Should().Equal(closestAndAboveTheAmount);
        }

        [Fact]
        public void SelectUTXOsByClosest_WhenTheUTXOBiggerThanTheAmountIsNotTheClosest_AccumulatesTheClosestOnes()
        {
            // Arrange: distances to the 50_000 requested are 5_000 | 10_000 | 30_000, so the UTXO above the
            // amount is the furthest one away
            var closest = UtxoOf(45_000);
            var secondClosest = UtxoOf(40_000);
            var aboveTheAmountButFurthest = UtxoOf(80_000);

            // Act
            var selectedUTXOs = SelectByClosest(closest, secondClosest, aboveTheAmountButFurthest);

            // Assert: the two closest ones are taken in order (45_000 leaves 5_000 to cover, which the
            // second one completes), and the UTXO above the amount is never reached
            selectedUTXOs.Should().Equal(closest, secondClosest);
        }

        public static TheoryData<string, Func<Wallet, long, List<UTXO>, ILogger, List<UTXO>>> Algorithms => new()
        {
            { nameof(UTXOSelectionAlgorithms.SelectUTXOsByOldest), UTXOSelectionAlgorithms.SelectUTXOsByOldest },
            { nameof(UTXOSelectionAlgorithms.SelectUTXOsByClosest), UTXOSelectionAlgorithms.SelectUTXOsByClosest }
        };

        [Theory]
        [MemberData(nameof(Algorithms))]
        public void SelectUTXOs_WithoutAvailableUTXOs_SelectsNothing(
            string algorithmName, Func<Wallet, long, List<UTXO>, ILogger, List<UTXO>> selectUTXOs)
        {
            // Act
            var selectedUTXOs = selectUTXOs(_wallet, RequestedSats, new List<UTXO>(), _logger);

            // Assert
            selectedUTXOs.Should().BeEmpty($"{algorithmName} has nothing to select from");
        }

        [Theory]
        [MemberData(nameof(Algorithms))]
        public void SelectUTXOs_WithNotEnoughFunds_SelectsNothing(
            string algorithmName, Func<Wallet, long, List<UTXO>, ILogger, List<UTXO>> selectUTXOs)
        {
            // Arrange: 49_999 sats available for the 50_000 requested
            var availableUTXOs = new List<UTXO> { UtxoOf(29_999), UtxoOf(20_000) };

            // Act
            var selectedUTXOs = selectUTXOs(_wallet, RequestedSats, availableUTXOs, _logger);

            // Assert: a partial selection would build an underfunded transaction, so nothing is selected
            selectedUTXOs.Should().BeEmpty($"{algorithmName} cannot cover the requested amount");
        }
    }
}
