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

using Microsoft.Extensions.Logging;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Services;

public class FeeEngineStateServiceTests
{
    private readonly Mock<IChannelFeeStateRepository> _feeStateRepository = new();
    private readonly Mock<ILogger<FeeEngineStateService>> _logger = new();

    private FeeEngineStateService Sut()
    {
        _feeStateRepository.Setup(r => r.DeleteByChannelId(It.IsAny<int>())).ReturnsAsync(1);
        _feeStateRepository.Setup(r => r.DeleteByManagedNodePubKey(It.IsAny<string>())).ReturnsAsync(1);
        return new FeeEngineStateService(_feeStateRepository.Object, _logger.Object);
    }

    [Fact]
    public async Task PurgeChannelStateIfDisabled_Disabled_DeletesState()
    {
        var channel = new Channel { Id = 5, ChanId = 123, IsDynamicFeeEnabled = false };

        await Sut().PurgeChannelStateIfDisabled(channel);

        _feeStateRepository.Verify(r => r.DeleteByChannelId(5), Times.Once);
    }

    [Fact]
    public async Task PurgeChannelStateIfDisabled_Enabled_DoesNothing()
    {
        var channel = new Channel { Id = 5, ChanId = 123, IsDynamicFeeEnabled = true };

        await Sut().PurgeChannelStateIfDisabled(channel);

        _feeStateRepository.Verify(r => r.DeleteByChannelId(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task PurgeNodeStateIfDisabled_NodeDisabled_DeletesAllChannelStates()
    {
        // Disabled node — even with the fee gate on, the node manages nothing while disabled.
        var node = new Node { PubKey = "02a", Name = "A", IsNodeDisabled = true, DynamicFeeManagementEnabled = true };

        await Sut().PurgeNodeStateIfDisabled(node);

        _feeStateRepository.Verify(r => r.DeleteByManagedNodePubKey("02a"), Times.Once);
    }

    [Fact]
    public async Task PurgeNodeStateIfDisabled_FeeManagementOff_DeletesAllChannelStates()
    {
        var node = new Node { PubKey = "02a", Name = "A", IsNodeDisabled = false, DynamicFeeManagementEnabled = false };

        await Sut().PurgeNodeStateIfDisabled(node);

        _feeStateRepository.Verify(r => r.DeleteByManagedNodePubKey("02a"), Times.Once);
    }

    [Fact]
    public async Task PurgeNodeStateIfDisabled_ActiveAndManaging_DoesNothing()
    {
        var node = new Node { PubKey = "02a", Name = "A", IsNodeDisabled = false, DynamicFeeManagementEnabled = true };

        await Sut().PurgeNodeStateIfDisabled(node);

        _feeStateRepository.Verify(r => r.DeleteByManagedNodePubKey(It.IsAny<string>()), Times.Never);
    }
}
