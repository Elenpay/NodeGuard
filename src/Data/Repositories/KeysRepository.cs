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

using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Key = NodeGuard.Data.Models.Key;

namespace NodeGuard.Data.Repositories
{
    public class KeyRepository : IKeyRepository
    {
        private readonly IRepository<Key> _repository;
        private readonly ILogger<KeyRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IInternalWalletRepository _internalWalletRepository;

        public KeyRepository(IRepository<Key> repository,
            ILogger<KeyRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IInternalWalletRepository internalWalletRepository)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _internalWalletRepository = internalWalletRepository;
        }

        public async Task<Key?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Keys.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<Key>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Keys.ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(Key type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            try
            {
                var xpub = new BitcoinExtPubKey(type.XPUB, CurrentNetworkHelper.GetCurrentNetwork());
            }
            catch (Exception e)
            {
                const string errorWhileValidatingXpub = "Error while validating XPUB";

                _logger.LogError(errorWhileValidatingXpub);

                return (false, errorWhileValidatingXpub);
            }

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<Key> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(Key type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<Key> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(Key type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            type.Wallets = null;

            return _repository.Update(type, applicationDbContext);
        }

        public async Task<List<Key>> GetUserKeys(ApplicationUser applicationUser)
        {
            if (applicationUser == null) throw new ArgumentNullException(nameof(applicationUser));

            await using var applicationDbContext = _dbContextFactory.CreateDbContext();

            var result = await applicationDbContext.Keys.Include(x => x.Wallets).Where(x => x.UserId == applicationUser.Id).ToListAsync();

            return result;
        }

        public async Task<Key> GetCurrentInternalWalletKey(string accountId)
        {
            await using var applicationDbContext = _dbContextFactory.CreateDbContext();

            var internalWallet = await _internalWalletRepository.GetCurrentInternalWallet();

            if (internalWallet == null)
                return null;

            var result = await applicationDbContext.Keys
                .OrderByDescending(x => x.Id)
                .SingleOrDefaultAsync(x => x.InternalWalletId == internalWallet.Id && x.Path == internalWallet.GetKeyPathForAccount(accountId));

            //If they key does not exist we should create it
            if (result == null)
            {
                result = new Key
                {
                    CreationDatetime = DateTimeOffset.Now,
                    InternalWalletId = internalWallet.Id,
                    UpdateDatetime = DateTimeOffset.Now,
                    Name = "NodeGuard Derived Co-signing Key",
                    XPUB = internalWallet.GetXpubForAccount(accountId),
                    MasterFingerprint = internalWallet.MasterFingerprint,
                    //Derivation path
                    Path = internalWallet.GetKeyPathForAccount(accountId),
                };
            }

            return result;
        }
    }
}