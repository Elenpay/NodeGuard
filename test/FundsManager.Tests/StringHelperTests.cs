using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using FundsManager.Helpers;

namespace FundsManager.Tests
{
    public class StringHelperTests
    {
        [Theory]
        //Even
        [InlineData("02abfc135d0b42896a812e453485213fd5d2333d7943ceae68287d4c0d5574e0f2", "02abfc135d......0d5574e0f2", 10)]
        [InlineData("03e81f50b6cc38c84ada454e48aae720e726fc2990fe923f9406fe0d0e35d0f7e0", "03e81f50b6......0e35d0f7e0", 10)]
        [InlineData("02abfc135d0b42896a812e453485213fd5d2333d7943ceae68287d4c0d5574e0f2", "02a......0f2", 3)]
        [InlineData("03e81f50b6cc38c84ada454e48aae720e726fc2990fe923f9406fe0d0e35d0f7e0", "03e......7e0", 3)]
        //Odd lenght
        [InlineData("02abfc135d0b42896a812e453485213f5d2333d7943ceae68287d4c0d5574e0f2", "02abfc135d......0d5574e0f2", 10)]
        [InlineData("03e81f50b6cc38c84ada454e48aae720726fc2990fe923f9406fe0d0e35d0f7e0", "03e81f50b6......0e35d0f7e0", 10)]
        [InlineData("02abfc135d0b42896a812e453485213f5d2333d7943ceae68287d4c0d5574e0f2", "02a......0f2", 3)]
        [InlineData("03e81f50b6cc38c84ada454e48aae720726fc2990fe923f9406fe0d0e35d0f7e0", "03e......7e0", 3)]
        public void TruncateHeadAndTail_Positive(string pubkey, string expectedTruncated, int numberOfCharactersToDisplay)
        {
            //Arrange

            //Act
            var result = StringHelper.TruncateHeadAndTail(pubkey, numberOfCharactersToDisplay);

            //Assert

            result.Should().Be(expectedTruncated);
        }

        [Theory]
        [InlineData("", "", 3)]
        [InlineData(" ", "", 3)]
        [InlineData(null, "", 3)]
        public void TruncateHeadAndTail_Negative(string pubkey, string expectedTruncated, int numberOfCharactersToDisplay)
        {
            //Arrange

            //Act
            var result = StringHelper.TruncateHeadAndTail(pubkey, numberOfCharactersToDisplay);

            //Assert

            result.Should().Be(expectedTruncated);
        }
    }
}