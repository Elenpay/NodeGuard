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

using AutoFixture;
using AutoMapper;
using FluentAssertions;
using FundsManager.Automapper;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Nodeguard;
using NSubstitute.ExceptionExtensions;
using LiquidityRule = FundsManager.Data.Models.LiquidityRule;
using NodeGuardService = FundsManager.rpc.NodeGuardService;

namespace FundsManager.Tests.rpc
{
    
    public class NodeGuardServiceTests
    {
        private readonly Fixture _fixture;
        private readonly Mock<ILogger<NodeGuardService>> _logger;
        private readonly IMapper _mockMapper;

        public NodeGuardServiceTests()
        {
            _logger = new Mock<ILogger<NodeGuardService>>();
            _fixture = new Fixture();
            _fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList()
                .ForEach(b => _fixture.Behaviors.Remove(b));
            _fixture.Behaviors.Add(new OmitOnRecursionBehavior());

            _mockMapper = new MapperConfiguration(config => { config.AddProfile<MapperProfile>(); }).CreateMapper();
        }


        [Fact]
        public async Task GetLiquidityRules_NoPubkey()
        {
            //Arrange
            var liquidityRuleRepository = new Mock<ILiquidityRuleRepository>();
            liquidityRuleRepository.Setup(x =>
                x.GetByNodePubKey(It.IsAny<string>())).ReturnsAsync(new List<LiquidityRule>());

            var wallet = _fixture.Create<Wallet>();
            var walletRepository = new Mock<IWalletRepository>();

            walletRepository.Setup(x => x.GetById(It.IsAny<int>())).ReturnsAsync(wallet);


            var mockNodeGuardService =
                new NodeGuardService(_logger.Object, liquidityRuleRepository.Object, walletRepository.Object,
                    _mockMapper);
            var getLiquidityRulesRequest = new GetLiquidityRulesRequest
            {
                NodePubkey = string.Empty
            };
            var context = TestServerCallContext.Create();
            //Act

            await Assert.ThrowsAsync<RpcException>(() =>
                mockNodeGuardService.GetLiquidityRules(getLiquidityRulesRequest, context));
        }

        [Fact]
        public async Task GetLiquidityRules_Success()
        {
            //Arrange
            var liquidityRuleRepository = new Mock<ILiquidityRuleRepository>();
            var liquidityRules = new List<LiquidityRule>
            {
                _fixture.Create<LiquidityRule>()
            };

            liquidityRuleRepository.Setup(x =>
                x.GetByNodePubKey(It.IsAny<string>())).ReturnsAsync(liquidityRules);

            var wallet = _fixture.Create<Wallet>();
            var walletRepository = new Mock<IWalletRepository>();

            walletRepository.Setup(x => x.GetById(It.IsAny<int>())).ReturnsAsync(wallet);


            var mockNodeGuardService =
                new NodeGuardService(_logger.Object, liquidityRuleRepository.Object, walletRepository.Object,
                    _mockMapper);
            var getLiquidityRulesRequest = _fixture.Create<GetLiquidityRulesRequest>();
            var context = TestServerCallContext.Create();
            //Act

            var result = await mockNodeGuardService.GetLiquidityRules(getLiquidityRulesRequest, context);

            //Assert
            result.Should().NotBeNull();
            result.LiquidityRules.Should().NotBeNull();
            result.LiquidityRules.Should().NotBeEmpty();

            result.LiquidityRules.First().MinimumLocalBalance.Should().Be((float) (liquidityRules.First().MinimumLocalBalance));
        }
        [Fact]
        public async Task GetLiquidityRules_Exception()
        {
            //Arrange
            var liquidityRuleRepository = new Mock<ILiquidityRuleRepository>();
            var liquidityRules = new List<LiquidityRule>
            {
            };

            liquidityRuleRepository.Setup(x =>
                x.GetByNodePubKey(It.IsAny<string>())).ThrowsAsync(new Exception("test"));

            var wallet = _fixture.Create<Wallet>();
            var walletRepository = new Mock<IWalletRepository>();

            walletRepository.Setup(x => x.GetById(It.IsAny<int>())).ReturnsAsync(wallet);


            var mockNodeGuardService =
                new NodeGuardService(_logger.Object, liquidityRuleRepository.Object, walletRepository.Object,
                    _mockMapper);
            var getLiquidityRulesRequest = _fixture.Create<GetLiquidityRulesRequest>();
            var context = TestServerCallContext.Create();
            //Act

            await Assert.ThrowsAnyAsync<RpcException>(() => mockNodeGuardService.GetLiquidityRules(getLiquidityRulesRequest, context));
          
        }      
        
        [Fact]
        public async Task GetNewWalletAddress_Success()
        {
            throw new NotImplementedException();

        }   
    }
}