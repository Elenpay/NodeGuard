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

﻿using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class FUTXORepository : IFMUTXORepository
    {
        private readonly IRepository<FMUTXO> _repository;
        private readonly ILogger<FUTXORepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public FUTXORepository(IRepository<FMUTXO> repository,
            ILogger<FUTXORepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<FMUTXO?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.FMUTXOs.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<FMUTXO>> GetAll()
        {
            throw new NotImplementedException();
        }

        public async Task<(bool, string?)> AddAsync(FMUTXO type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<FMUTXO> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(FMUTXO type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<FMUTXO> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(FMUTXO type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetUpdateDatetime();

            return _repository.Update(type, applicationDbContext);
        }

        public async Task<List<FMUTXO>> GetLockedUTXOs(int? ignoredRequestId = null, BitcoinRequestType? bitcoinRequest = null)
        {
            try
            {
                await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

                var walletWithdrawalRequestsLockedUTXOs = await applicationDbContext.WalletWithdrawalRequests
                    .Include(x => x.UTXOs)
                    .Where(x => ignoredRequestId == null || bitcoinRequest != BitcoinRequestType.WalletWithdrawal || x.Id == ignoredRequestId && x.Status == WalletWithdrawalRequestStatus.Pending ||
                                x.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.UTXOs).ToListAsync();

                var channelOperationRequestsLockedUTXOs = await applicationDbContext.ChannelOperationRequests.Include(x => x.Utxos)
                    .Where(x => ignoredRequestId == null || bitcoinRequest != BitcoinRequestType.ChannelOperation || x.Id == ignoredRequestId
                                && x.Status == ChannelOperationRequestStatus.Pending ||
                                x.Status == ChannelOperationRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.Utxos).ToListAsync();


                var result = walletWithdrawalRequestsLockedUTXOs.Union(channelOperationRequestsLockedUTXOs).ToList();

                return result;
            }
            catch (Exception e)
            {
                _logger.LogError("Error while getting locked UTXOs: {Message}", e.Message);
                return new();
            }
        }
    }
}