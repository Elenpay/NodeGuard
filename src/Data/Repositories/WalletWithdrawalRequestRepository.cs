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

using AutoMapper;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Exception = System.Exception;

namespace NodeGuard.Data.Repositories
{
    public class WalletWithdrawalRequestRepository : IWalletWithdrawalRequestRepository
    {
        private readonly IRepository<WalletWithdrawalRequest> _repository;
        private readonly ILogger<WalletWithdrawalRequestRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IMapper _mapper;
        private readonly NotificationService _notificationService;
        private readonly INBXplorerService _nBXplorerService;

        public WalletWithdrawalRequestRepository(IRepository<WalletWithdrawalRequest> repository,
            ILogger<WalletWithdrawalRequestRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IMapper mapper,
            NotificationService notificationService,
            INBXplorerService nBXplorerService
            )
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _mapper = mapper;
            _notificationService = notificationService;
            _nBXplorerService = nBXplorerService;
        }

        public async Task<WalletWithdrawalRequest?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var request = await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet)
                .ThenInclude(x => x.InternalWallet)
                .Include(x => x.Wallet)
                .ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .SingleOrDefaultAsync(x => x.Id == id);

            return request;
        }

        public async Task<List<WalletWithdrawalRequest>> GetByIds(List<int> ids)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests.Where(wr => ids.Contains(wr.Id)).ToListAsync();
        }

        public async Task<List<WalletWithdrawalRequest>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<List<WalletWithdrawalRequest>> GetUnsignedPendingRequestsByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .Where(request => request.Wallet.Keys.Any(key => key.User != null && key.User.Id == userId) &&
                                  (request.Status == WalletWithdrawalRequestStatus.Pending || request.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending) &&
                                  request.WalletWithdrawalRequestPSBTs.All(signature => signature.SignerId != userId))
                .AsSplitQuery()
                .ToListAsync();
        }
        
        public async Task<List<WalletWithdrawalRequest>> GetAllUnsignedPendingRequests()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .Where(request => request.Status == WalletWithdrawalRequestStatus.Pending || request.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(WalletWithdrawalRequest type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            //Verify that the wallet has enough funds calling nbxplorer
            var wallet = await applicationDbContext.Wallets.Include(x=> x.Keys).SingleOrDefaultAsync(x => x.Id == type.WalletId);

            if (wallet == null)
            {
                return (false, "The wallet could not be found.");
            }

            var derivationStrategyBase = wallet.GetDerivationStrategy();

            if (derivationStrategyBase == null)
            {
                return (false, "The wallet does not have a derivation strategy.");
            }

            var balance = await _nBXplorerService.GetBalanceAsync(derivationStrategyBase, default);

            if (balance == null)
            {
                return (false, "Balance could not be retrieved from the wallet.");
            }

            var requestMoneyAmount = new Money(type.Amount, MoneyUnit.BTC);

            if ((Money) balance.Confirmed < requestMoneyAmount)
            {
                return (false, $"The wallet {type.Wallet.Name} does not have enough funds to complete this withdrawal request. The wallet has {balance.Confirmed} BTC and the withdrawal request is for {requestMoneyAmount} BTC.");
            }


            var valueTuple = await _repository.AddAsync(type, applicationDbContext);
            if (!wallet.IsHotWallet)
                await _notificationService.NotifyRequestSigners(type.WalletId, "/withdrawals");

            return valueTuple;
        }

        public async Task<(bool, string?)> AddRangeAsync(List<WalletWithdrawalRequest> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(WalletWithdrawalRequest type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<WalletWithdrawalRequest> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(WalletWithdrawalRequest type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            //Automapper to remove collections
            var strippedType = _mapper.Map<WalletWithdrawalRequest, WalletWithdrawalRequest>(type);
            type.SetUpdateDatetime();

            return _repository.Update(strippedType, applicationDbContext);
        }

        public async Task<(bool, string?)> AddUTXOs(IBitcoinRequest type, List<FMUTXO> utxos)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (utxos.Count == 0) throw new ArgumentException("Value cannot be an empty collection.", nameof(utxos));

            (bool, string?) result = (true, null);

            try
            {
                await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

                var request = await applicationDbContext.WalletWithdrawalRequests.Include(x => x.UTXOs)
                    .SingleOrDefaultAsync(x => x.Id == type.Id);
                if (request != null)
                {
                    if (!request.UTXOs.Any())
                    {
                        request.UTXOs = utxos;
                    }
                    else
                    {
                        request.UTXOs.AddRange(utxos.Except(request.UTXOs));
                    }

                    applicationDbContext.Update(request);

                    await applicationDbContext.SaveChangesAsync();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while adding UTXOs ({Utxos}) to op request: {RequestId}", utxos.Humanize(), type.Id);

                result.Item1 = false;
            }

            return result;
        }

        public async Task<(bool, List<FMUTXO>?)> GetUTXOs(IBitcoinRequest request)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            (bool, List<FMUTXO>?) result = (true, null);
            try
            {
                var walletWithdrawalRequest = await applicationDbContext.WalletWithdrawalRequests
                    .Include(r => r.UTXOs)
                    .FirstOrDefaultAsync(r => r.Id == request.Id);

                result.Item2 = walletWithdrawalRequest.UTXOs;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while getting UTXOs from wallet withdrawal request: {RequestId}",  request.Id);
                result.Item1 = false;
            }

            return result;
        }

        public async Task<List<WalletWithdrawalRequest>> GetPendingRequests()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var walletWithdrawalRequests = await applicationDbContext.WalletWithdrawalRequests
                .Where(request => request.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending
                                  || request.Status == WalletWithdrawalRequestStatus.Pending
                                  || request.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending)
                .Include(request => request.Wallet)
                .ToListAsync();

            return walletWithdrawalRequests;
        }

        public async Task<List<WalletWithdrawalRequest>> GetOnChainPendingWithdrawals()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var walletWithdrawalRequests = await applicationDbContext.WalletWithdrawalRequests
                .Where(request => request.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
                .Include(request => request.Wallet)
                .ToListAsync();

            return walletWithdrawalRequests;
        }
    }
}