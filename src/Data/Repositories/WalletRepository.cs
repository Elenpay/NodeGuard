/*
 * NodeGuard
 * Copyright (C) 2023  ClovrLabs
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
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 */

﻿using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class WalletRepository : IWalletRepository
    {
        private readonly IRepository<Wallet> _repository;
        private readonly ILogger<WalletRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IInternalWalletRepository _internalWalletRepository;
        private readonly IKeyRepository _keyRepository;

        public WalletRepository(IRepository<Wallet> repository,
            ILogger<WalletRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IInternalWalletRepository internalWalletRepository, IKeyRepository keyRepository)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _internalWalletRepository = internalWalletRepository;
            _keyRepository = keyRepository;
        }

        public async Task<Wallet?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Wallets.Include(x => x.InternalWallet)
                .Include(x => x.Keys)
                .FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<Wallet>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Wallets.Include(x => x.InternalWallet).Include(x => x.Keys).ToListAsync();
        }

        public async Task<List<Wallet>> GetAvailableWallets()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Wallets
                .Where(wallet => !wallet.IsArchived && !wallet.IsCompromised && wallet.IsFinalised)
                .Include(x => x.InternalWallet)
                .Include(x => x.Keys)
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(Wallet type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            try
            {
                await using var transaction = await applicationDbContext.Database.BeginTransactionAsync();

                //We add the internal wallet of the moment and its key
                var currentInternalWallet = (await _internalWalletRepository.GetCurrentInternalWallet());
                if (currentInternalWallet != null)
                    type.InternalWalletId = currentInternalWallet.Id;

                var currentInternalWalletKey = await _keyRepository.GetCurrentInternalWalletKey();

                type.Keys = new List<Key>();

                var addResult = await _repository.AddAsync(type, applicationDbContext);

                if (currentInternalWalletKey != null)
                    type.Keys.Add(currentInternalWalletKey);

                //We need to persist before updating a m-of-n relationship
                applicationDbContext.Update(type);
                await applicationDbContext.SaveChangesAsync();

                transaction.Commit();
            }
            catch (Exception e)
            {
                const string errorWhileAddingOnRepository = "Error while adding on repository";
                _logger.LogError(e, errorWhileAddingOnRepository);

                return (false, errorWhileAddingOnRepository);
            }

            return (true, null);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<Wallet> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(Wallet type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<Wallet> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(Wallet type, bool includeKeysUpdate = false)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetUpdateDatetime();
            if (!includeKeysUpdate)
                type.Keys?.Clear();

            type.ChannelOperationRequestsAsSource?.Clear();
            return _repository.Update(type, applicationDbContext);
        }

        public async Task<(bool, string?)> FinaliseWallet(Wallet selectedWalletToFinalise)
        {
            if (selectedWalletToFinalise == null) throw new ArgumentNullException(nameof(selectedWalletToFinalise));
            await using var applicationDbContext = _dbContextFactory.CreateDbContext();

            (bool, string?) result = (true, null);

            if (selectedWalletToFinalise.Keys.Count < selectedWalletToFinalise.MofN)
            {
                return (false, "Invalid number of keys for the given threshold");
            }

            selectedWalletToFinalise.IsFinalised = true;
            try
            {
                var (_, nbxplorerClient) = LightningHelper.GenerateNetwork();

                var derivationStrategyBase = selectedWalletToFinalise.GetDerivationStrategy();
                if (derivationStrategyBase == null)
                {
                    return (false, "Error while getting the derivation scheme");
                }

                await nbxplorerClient.TrackAsync(derivationStrategyBase);

                selectedWalletToFinalise.Keys = null;
                selectedWalletToFinalise.ChannelOperationRequestsAsSource = null;

                var updateResult = Update(selectedWalletToFinalise);

                if (updateResult.Item1 == false)
                {
                    result = (false, "Error while finalising wallet");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while finalising wallet: {WalletId}", selectedWalletToFinalise.Id);

                result = (false, "Error while finalising wallet");
            }

            return result;
        }
    }
}