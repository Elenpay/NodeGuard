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

﻿using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace NodeGuard.Data.Repositories
{
    public class FUTXORepository : IUTXORepository
    {
        private readonly IRepository<UTXO> _repository;
        private readonly ILogger<FUTXORepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public FUTXORepository(IRepository<UTXO> repository,
            ILogger<FUTXORepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<UTXO?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.UTXOs.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<UTXO>> GetAll()
        {
            throw new NotImplementedException();
        }

        public async Task<(bool, string?)> AddAsync(UTXO type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<UTXO> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(UTXO type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<UTXO> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(UTXO type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetUpdateDatetime();

            return _repository.Update(type, applicationDbContext);
        }

        public async Task<List<UTXO>> GetLockedUTXOs(int? ignoredWalletWithdrawalRequestId = null, int? ignoredChannelOperationRequestId = null)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var walletWithdrawalRequestsLockedUTXOs = new List<UTXO>();
            if (ignoredWalletWithdrawalRequestId == null)
            {
                walletWithdrawalRequestsLockedUTXOs = await applicationDbContext.WalletWithdrawalRequests
                    .Include(x => x.UTXOs)
                    .Where(x => x.Status == WalletWithdrawalRequestStatus.Pending ||
                                x.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending ||
                                x.Status == WalletWithdrawalRequestStatus.FinalizingPSBT ||
                                x.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.UTXOs).ToListAsync();
            }
            else
            {
                walletWithdrawalRequestsLockedUTXOs = await applicationDbContext.WalletWithdrawalRequests
                    .Include(x => x.UTXOs)
                    .Where(x => x.Id != ignoredWalletWithdrawalRequestId
                                && x.Status == WalletWithdrawalRequestStatus.Pending ||
                                x.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending ||
                                x.Status == WalletWithdrawalRequestStatus.FinalizingPSBT ||
                                x.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.UTXOs).ToListAsync();
            }

            var channelOperationRequestsLockedUTXOs = new List<UTXO>();

            if (ignoredChannelOperationRequestId == null)
            {
                channelOperationRequestsLockedUTXOs = await applicationDbContext.ChannelOperationRequests.Include(x => x.Utxos)
                    .Where(x => x.Status == ChannelOperationRequestStatus.Pending ||
                                x.Status == ChannelOperationRequestStatus.PSBTSignaturesPending ||
                                x.Status == ChannelOperationRequestStatus.FinalizingPSBT ||
                                x.Status == ChannelOperationRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.Utxos).ToListAsync();
            }
            else
            {
                channelOperationRequestsLockedUTXOs = await applicationDbContext.ChannelOperationRequests.Include(x => x.Utxos)
                    .Where(x => x.Id != ignoredChannelOperationRequestId
                        && x.Status == ChannelOperationRequestStatus.Pending ||
                                x.Status == ChannelOperationRequestStatus.PSBTSignaturesPending ||
                                x.Status == ChannelOperationRequestStatus.FinalizingPSBT ||
                                x.Status == ChannelOperationRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.Utxos).ToListAsync();
            }

            var result = walletWithdrawalRequestsLockedUTXOs.Union(channelOperationRequestsLockedUTXOs).ToList();

            return result;
        }
    }
}