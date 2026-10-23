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
                .Include(x => x.WalletWithdrawalRequestDestinations)
                .Include(x => x.UTXOs)
                .SingleOrDefaultAsync(x => x.Id == id);

            return request;
        }

        public async Task<List<WalletWithdrawalRequest>> GetByIds(List<int> ids)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests.Where(wr => ids.Contains(wr.Id)).ToListAsync();
        }

        public async Task<List<WalletWithdrawalRequest>> GetByReferenceIds(List<string> referenceIds)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            // RBF replacements inherit the reference id of the request they replace, so a reference id matches a whole
            // chain. Every request of it is returned, oldest first, each reporting its own (derived) status.
            return await applicationDbContext.WalletWithdrawalRequests
                .Where(wr => !string.IsNullOrEmpty(wr.ReferenceId) && referenceIds.Contains(wr.ReferenceId))
                .OrderBy(wr => wr.Id)
                .ToListAsync();
        }

        public async Task<WalletWithdrawalRequest?> GetByTxHash(string txHash)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests
                .FirstOrDefaultAsync(wr => wr.TxId == txHash);
        }


        public async Task<List<WalletWithdrawalRequest>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .Include(x => x.WalletWithdrawalRequestDestinations)
                .Include(x => x.UTXOs)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<(List<WalletWithdrawalRequest> Requests, int TotalCount)> GetPaginatedAsync(
            int pageNumber,
            int pageSize,
            IEnumerable<int>? excludedRequestIds = null,
            WalletWithdrawalRequestStatus? status = null,
            int? walletId = null,
            string? userId = null,
            DateTimeOffset? fromDate = null,
            DateTimeOffset? toDate = null)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            if (pageNumber < 1)
            {
                pageNumber = 1;
            }

            if (pageSize < 1)
            {
                pageSize = 1;
            }

            var query = applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .Include(x => x.WalletWithdrawalRequestDestinations)
                .Include(x => x.UTXOs)
                .AsSplitQuery()
                .AsQueryable();

            if (excludedRequestIds != null)
            {
                var excludedIds = excludedRequestIds.Where(id => id > 0).Distinct().ToList();
                if (excludedIds.Count > 0)
                {
                    query = query.Where(x => !excludedIds.Contains(x.Id));
                }
            }

            if (status.HasValue)
                query = query.Where(x => x.Status == status.Value);

            if (walletId.HasValue)
                query = query.Where(x => x.WalletId == walletId.Value);

            if (!string.IsNullOrEmpty(userId))
                query = query.Where(x => x.UserRequestorId == userId);

            if (fromDate.HasValue)
                query = query.Where(x => x.CreationDatetime >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(x => x.CreationDatetime <= toDate.Value);

            query = query.OrderByDescending(x => x.CreationDatetime);

            var totalCount = await query.CountAsync();

            var items = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (items, totalCount);
        }

        public async Task<List<WalletWithdrawalRequest>> GetUnsignedPendingRequestsByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .Include(x => x.WalletWithdrawalRequestDestinations)
                .Where(request => request.Wallet != null
                                && request.Wallet.Keys.Count(key => userId == key.UserId) > request.WalletWithdrawalRequestPSBTs.Count(req => req.SignerId == userId)
                                && (request.Status == WalletWithdrawalRequestStatus.Pending || request.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending))
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
                .Include(x => x.WalletWithdrawalRequestDestinations)
                .Where(request => request.Status == WalletWithdrawalRequestStatus.Pending || request.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(WalletWithdrawalRequest type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            // Set the types for the destinations
            type.WalletWithdrawalRequestDestinations?.ForEach(destination =>
            {
                destination.SetCreationDatetime();
                destination.SetUpdateDatetime();
            });

            //Verify that the wallet has enough funds calling nbxplorer
            var wallet = await applicationDbContext.Wallets.Include(x => x.Keys).SingleOrDefaultAsync(x => x.Id == type.WalletId);

            if (wallet == null)
            {
                return (false, "The wallet could not be found.");
            }

            type.Wallet = wallet;

            var derivationStrategyBase = wallet.GetDerivationStrategy();

            if (derivationStrategyBase == null)
            {
                return (false, "The wallet does not have a derivation strategy.");
            }

            // An RBF replacement (fee bump or cancellation) re-spends inputs that are already locked by the request it
            // replaces, so the wallet's confirmed balance says nothing about whether it can be funded.
            if (type.BumpingWalletWithdrawalRequestId == null)
            {
                var balance = await _nBXplorerService.GetBalanceAsync(derivationStrategyBase, default);

                if (balance == null)
                {
                    return (false, "Balance could not be retrieved from the wallet.");
                }

                var requestMoneyAmount = new Money(type.TotalAmount, MoneyUnit.BTC);

                if ((Money)balance.Confirmed < requestMoneyAmount)
                {
                    return (false, $"The wallet {type.Wallet.Name} does not have enough funds to complete this withdrawal request. The wallet has {balance.Confirmed} BTC and the withdrawal request is for {requestMoneyAmount} BTC.");
                }
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

            // Stamp before mapping, otherwise the stripped copy that is persisted keeps the old UpdateDatetime.
            type.SetUpdateDatetime();

            //Automapper to remove collections
            var strippedType = _mapper.Map<WalletWithdrawalRequest, WalletWithdrawalRequest>(type);

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
                _logger.LogError(e, "Error while getting UTXOs from wallet withdrawal request: {RequestId}", request.Id);
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
                .Include(x => x.WalletWithdrawalRequestDestinations)
                .ToListAsync();

            return walletWithdrawalRequests;
        }

        public async Task<List<WalletWithdrawalRequest>> GetRbfChainAsync(int requestId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
            var requests = applicationDbContext.WalletWithdrawalRequests.AsNoTracking();

            // Up to the first request of the chain, then down through every replacement, level by level. One query
            // per hop; chains are a handful of rows.
            var root = await requests.SingleOrDefaultAsync(x => x.Id == requestId);
            if (root == null) return new List<WalletWithdrawalRequest>();

            var chain = new Dictionary<int, WalletWithdrawalRequest> { [root.Id] = root };
            while (root.BumpingWalletWithdrawalRequestId is { } replacedId && !chain.ContainsKey(replacedId))
            {
                var replaced = await requests.SingleOrDefaultAsync(x => x.Id == replacedId);
                if (replaced == null) break;

                chain[replaced.Id] = replaced;
                root = replaced;
            }

            var expanded = new HashSet<int> { root.Id };
            var frontier = new List<int> { root.Id };
            while (frontier.Count > 0)
            {
                var parents = frontier;
                var children = await requests
                    .Where(x => x.BumpingWalletWithdrawalRequestId != null && parents.Contains(x.BumpingWalletWithdrawalRequestId.Value))
                    .ToListAsync();

                frontier = new List<int>();
                foreach (var child in children)
                {
                    chain.TryAdd(child.Id, child);
                    if (expanded.Add(child.Id)) frontier.Add(child.Id);
                }
            }

            return chain.Values.OrderBy(x => x.Id).ToList();
        }

        public async Task<List<int>> GetLiveRbfLinkedRequestIdsAsync()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var live = WalletWithdrawalRequest.LiveStatuses;
            var requests = applicationDbContext.WalletWithdrawalRequests;
            return await requests
                .Where(x => live.Contains(x.Status)
                            && (x.BumpingWalletWithdrawalRequestId != null
                                || requests.Any(r => r.BumpingWalletWithdrawalRequestId == x.Id)))
                .Select(x => x.Id)
                .ToListAsync();
        }

        public async Task<ILookup<int, WalletWithdrawalRequest>> GetReplacementsAsync(IEnumerable<int> requestIds)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var ids = requestIds.Distinct().ToList();
            var replacements = await applicationDbContext.WalletWithdrawalRequests
                .AsNoTracking()
                .Where(x => x.BumpingWalletWithdrawalRequestId != null && ids.Contains(x.BumpingWalletWithdrawalRequestId.Value))
                .ToListAsync();

            return replacements.ToLookup(x => x.BumpingWalletWithdrawalRequestId!.Value);
        }

        public async Task<List<WalletWithdrawalRequest>> GetOnChainPendingWithdrawals()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var walletWithdrawalRequests = await applicationDbContext.WalletWithdrawalRequests
                .Where(request => request.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
                .Include(request => request.Wallet)
                .Include(x => x.WalletWithdrawalRequestDestinations)
                .ToListAsync();

            return walletWithdrawalRequests;
        }
    }
}
