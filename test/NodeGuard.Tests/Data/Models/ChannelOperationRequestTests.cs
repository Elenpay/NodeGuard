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

namespace NodeGuard.Tests;

public class ChannelOperationRequestTests
{
    [Fact]
    public void GetSingleTemplatePsbt_NoRows_ReturnsNull()
    {
        new ChannelOperationRequest().GetSingleTemplatePsbt().Should().BeNull();
        new ChannelOperationRequest { ChannelOperationRequestPsbts = new List<ChannelOperationRequestPSBT>() }
            .GetSingleTemplatePsbt().Should().BeNull();
    }

    [Fact]
    public void GetSingleTemplatePsbt_OneTemplateAmongApprovals_ReturnsIt()
    {
        var template = new ChannelOperationRequestPSBT { Id = 2, IsTemplatePSBT = true, PSBT = "template" };
        var request = new ChannelOperationRequest
        {
            ChannelOperationRequestPsbts = new List<ChannelOperationRequestPSBT>
            {
                new() { Id = 1, PSBT = "approval" },
                template,
                new() { Id = 3, IsInternalWalletPSBT = true, PSBT = "internal" },
            },
        };

        request.GetSingleTemplatePsbt().Should().BeSameAs(template);
    }

    [Fact]
    public void GetSingleTemplatePsbt_TwoTemplates_Throws()
    {
        var request = new ChannelOperationRequest
        {
            Id = 704,
            ChannelOperationRequestPsbts = new List<ChannelOperationRequestPSBT>
            {
                new() { Id = 1, IsTemplatePSBT = true, PSBT = "a" },
                new() { Id = 2, IsTemplatePSBT = true, PSBT = "b" },
            },
        };

        request.Invoking(r => r.GetSingleTemplatePsbt())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*704*more than one template*");
    }
}
