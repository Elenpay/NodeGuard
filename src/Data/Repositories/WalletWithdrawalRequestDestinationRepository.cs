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
using Microsoft.EntityFrameworkCore;

namespace NodeGuard.Data.Repositories
{
    public class WalletWithdrawalRequestDestinationRepository : IWalletWithdrawalRequestDestinationRepository
    {
        private readonly IRepository<WalletWithdrawalRequestDestination> _repository;
        private readonly ILogger<WalletWithdrawalRequestDestinationRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public WalletWithdrawalRequestDestinationRepository(IRepository<WalletWithdrawalRequestDestination> repository,
            ILogger<WalletWithdrawalRequestDestinationRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<WalletWithdrawalRequestDestination?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var destination = await applicationDbContext.WalletWithdrawalRequestDestinations
                .Include(x => x.WalletWithdrawalRequest)
                .SingleOrDefaultAsync(x => x.Id == id);

            return destination;
        }

        public async Task<List<WalletWithdrawalRequestDestination>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequestDestinations
                .Include(x => x.WalletWithdrawalRequest)
                .ToListAsync();
        }

        public async Task<List<WalletWithdrawalRequestDestination>> GetByWalletWithdrawalRequestId(int walletWithdrawalRequestId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequestDestinations
                .Include(x => x.WalletWithdrawalRequest)
                .Where(x => x.WalletWithdrawalRequestId == walletWithdrawalRequestId)
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(WalletWithdrawalRequestDestination type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<WalletWithdrawalRequestDestination> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            foreach (var destination in type)
            {
                destination.SetCreationDatetime();
                destination.SetUpdateDatetime();
            }

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(WalletWithdrawalRequestDestination type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<WalletWithdrawalRequestDestination> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(WalletWithdrawalRequestDestination type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetUpdateDatetime();

            return _repository.Update(type, applicationDbContext);
        }
    }
}
