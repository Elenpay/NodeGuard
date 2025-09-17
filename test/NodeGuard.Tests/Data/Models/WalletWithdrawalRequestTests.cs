// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

using FluentAssertions;
using NodeGuard.Data.Models;

namespace NodeGuard.Tests;

public class WalletWithdrawalRequestTests
{
    [Fact]
    public Task SignatureCounter_Positive_RequiresInternalwallet()
    {


        var request = new WalletWithdrawalRequest
        {
            Wallet = new Wallet
            {
                Keys = new List<Key>
                {
                    new Key(),
                    new Key(),
                    new Key()
                }
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT()
            }
        };
        // Act

        request.Wallet.MofN = 3;

        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().Be(true);
        request.AreAllRequiredHumanSignaturesCollected.Should().Be(true);
        request.NumberOfSignaturesCollected.Should().Be(2);
        return Task.CompletedTask;
    }

    [Fact]
    public Task SignatureCounter_Positive_NotRequiresInternalwallet()
    {
        // Arrange

        var request = new WalletWithdrawalRequest
        {
            Wallet = new Wallet
            {
                Keys = new List<Key>
                {
                    new Key(),
                    new Key(),
                    new Key()
                }
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT()
            }
        };
        // Act

        request.Wallet.MofN = 2;

        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().Be(false);
        request.AreAllRequiredHumanSignaturesCollected.Should().Be(true);
        request.NumberOfSignaturesCollected.Should().Be(2);
        return Task.CompletedTask;
    }

    [Fact]
    public Task SignatureCount_Negative_NotRequiresInternalWallet()
    {
        // Arrange
        var request = new WalletWithdrawalRequest
        {
            Wallet = new Wallet
            {
                Keys = new List<Key>
                {
                    new Key(),
                    new Key(),
                    new Key()
                }
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT()
            }
        };
        // Act

        request.Wallet.IsHotWallet = false;
        request.Wallet.MofN = 2;

        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;
        request.WalletWithdrawalRequestPSBTs.Last().IsFinalisedPSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().BeFalse();
        request.AreAllRequiredHumanSignaturesCollected.Should().BeFalse();
        request.NumberOfSignaturesCollected.Should().Be(1);
        return Task.CompletedTask;
    }

    [Fact]
    public Task SignatureCount_Negative_RequiresInternalWallet()
    {
        // Arrange
        var request = new WalletWithdrawalRequest
        {
            Wallet = new Wallet
            {
                Keys = new List<Key>
                {
                    new Key(),
                    new Key(),
                    new Key()
                }
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT()
            }
        };
        // Act
        request.Wallet.IsHotWallet = false;
        request.Wallet.MofN = 3;

        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;
        request.WalletWithdrawalRequestPSBTs.Last().IsFinalisedPSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().BeTrue();
        request.AreAllRequiredHumanSignaturesCollected.Should().BeFalse();
        request.NumberOfSignaturesCollected.Should().Be(1);
        return Task.CompletedTask;
    }



}
