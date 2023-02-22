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

﻿using AutoFixture;
using AutoFixture.AutoNSubstitute;
using AutoFixture.Kernel;
using FluentAssertions;
using FundsManager.Helpers;
using NBitcoin;
using NBXplorer.Models;

namespace FundsManager.Tests
{
    public class LightningHelperTests
    {
        [Fact]
        public void UTXODuplicateTest_Duplicated()
        {
            //Arrange
            var fixture = new Fixture();
            var utxoFixture = new UTXO
            {
                Value = new Money(10),
                Outpoint = fixture.Create<OutPoint>(),
                Index = fixture.Create<int>(),
                ScriptPubKey = fixture.Create<Script>()
            };

            var utxoChanges = new UTXOChanges
            {
                Confirmed = new UTXOChange
                {
                    UTXOs = new List<UTXO>(new List<UTXO>
                    {
                        utxoFixture, utxoFixture, utxoFixture
                    })
                }
            };
            //Act

            utxoChanges.RemoveDuplicateUTXOs();
            //Assert

            utxoChanges.Confirmed.UTXOs.Count.Should().Be(1);
        }

        [Fact]
        public void UTXODuplicateTest_NoDuplicated()
        {
            //Arrange
            var fixture = new Fixture();
            var utxoFixture = new UTXO
            {
                Value = new Money(10),
                Outpoint = fixture.Create<OutPoint>(),
                Index = fixture.Create<int>(),
                ScriptPubKey = fixture.Create<Script>()
            };

            var utxoChanges = new UTXOChanges
            {
                Confirmed = new UTXOChange
                {
                    UTXOs = new List<UTXO>(new List<UTXO>
                    {
                        utxoFixture
                    })
                }
            };
            //Act

            utxoChanges.RemoveDuplicateUTXOs();
            //Assert

            utxoChanges.Confirmed.UTXOs.Count.Should().Be(1);
        }
        
        [Fact]
        public void CreateLightningClient_EndpointIsNull()
        {
            // Act
            var act = () => LightningHelper.CreateLightningClient(null);

            // Assert
            act
                .Should()
                .Throw<ArgumentException>()
                .WithMessage("Endpoint cannot be null");
        }

        [Fact]
        public void CreateLightningClient_ReturnsLightningClient()
        {
            // Act
            var result = LightningHelper.CreateLightningClient("10.0.0.1");

            // Assert
            result.Should().NotBeNull();
        }
    }
}