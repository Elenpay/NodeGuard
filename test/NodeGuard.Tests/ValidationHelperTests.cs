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



using NodeGuard.Helpers;

namespace NodeGuard.Tests;

public class ValidationHelperTests
{
    [Theory]
    [InlineData("Vpub5jXH7agassYy8drZWSH1BbUdtpFvcSzWf9waRUUghPmRwNVucoudtAyCv5UPvFi9DyXxZtfiJUaduvTMKCtUxj8ARgD1vcPAQTasVSmCuxK", true)]
    [InlineData("tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLS", false)]

    public void ValidateXPUBTest(string? xpub, bool expected)
    {
        //Arrange

        //Act
        var actual = ValidationHelper.ValidateXPUB(xpub);

        //Assert
        Assert.Equal(expected, actual);
    }




}
