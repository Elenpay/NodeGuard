using AutoFixture;
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
    }
}