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
using NodeGuard.Services;
using FluentAssertions;

namespace NodeGuard.Services
{
    public class LoopServiceTests
    {
        private readonly ILogger<LoopService> _logger = new Mock<ILogger<LoopService>>().Object;
        private readonly LoopService _loopService;

        public LoopServiceTests()
        {
            _loopService = new LoopService(_logger);
        }


        // Integration test - requires actual Loop server running
        [Fact]
        public async Task PingAsync_ValidNode_ReturnsTrue()
        {
            // Arrange
            var node = new Node
            {
                LoopEndpoint = Constants.BOB_LOOP,
                LoopMacaroon = Constants.BOB_LOOP_MACAROON
            };

            // Act
            var result = await _loopService.PingAsync(node);

            // Assert
            result.Should().BeTrue();
        }

        // Integration test - requires actual Loop server running
        [Fact]
        public async Task CreateSwapOutAsync_ValidRequest_ReturnsSwapResponse()
        {
            // Arrange
            var node = new Node
            {
                LoopEndpoint = Constants.BOB_LOOP,
                LoopMacaroon = Constants.BOB_LOOP_MACAROON
            };
            var request = new SwapOutRequest
            {
                Amount = 250000,
                Address = "bcrt1qm7yjlmepcljpqt43g0jr9l409yy07hfyv8pjf2",  // Replace with actual address
                MaxServiceFees = 5000
            };

            // Act
            var result = await _loopService.CreateSwapOutAsync(node, request);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().NotBeNullOrEmpty();
            result.Amount.Should().Be(request.Amount);
        }

        // Integration test - requires actual Loop server running
        [Fact]
        public async Task GetSwapAsync_ValidRequest_ReturnsSwapResponse()
        {
            // Arrange
            var node = new Node
            {
                LoopEndpoint = Constants.BOB_LOOP,
                LoopMacaroon = Constants.BOB_LOOP_MACAROON
            };
            var swapId = "7e12a04e3243868c0ee0fa2b550cdbe32691df6e81b082a07a2828eb3b731756";  // Replace with actual swap ID

            // Act
            var result = await _loopService.GetSwapAsync(node, swapId);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().NotBeNullOrEmpty();
        }
    }
}