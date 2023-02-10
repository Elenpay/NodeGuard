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

﻿using System;
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