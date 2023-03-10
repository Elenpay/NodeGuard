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
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Humanizer;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ChannelOperationRequestRepository : IChannelOperationRequestRepository
    {
        private readonly IRepository<ChannelOperationRequest> _repository;
        private readonly ILogger<ChannelOperationRequestRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IMapper _mapper;
        private readonly NotificationService _notificationService;

        public ChannelOperationRequestRepository(IRepository<ChannelOperationRequest> repository,
            ILogger<ChannelOperationRequestRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory, IMapper mapper, NotificationService notificationService)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _mapper = mapper;
            _notificationService = notificationService;
        }

        public async Task<ChannelOperationRequest?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var request = await applicationDbContext.ChannelOperationRequests.Include(x => x.SourceNode)
                .Include(x => x.DestNode)
                .Include(x => x.Wallet).ThenInclude(x => x.InternalWallet)
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.ChannelOperationRequestPsbts)
                .Include(x => x.Utxos)
                .FirstOrDefaultAsync(x => x.Id == id);

            return request;
        }

        public async Task<List<ChannelOperationRequest>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequests
                .Include(request => request.Wallet)
                .Include(request => request.SourceNode)
                .Include(request => request.DestNode)
                .Include(request => request.ChannelOperationRequestPsbts)
                .Include(x => x.Utxos)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<List<ChannelOperationRequest>> GetUnsignedPendingRequestsByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequests
                .Where(request => request.Wallet.Keys.Any(key => key.User != null && key.User.Id == userId) &&
                                  (request.Status == ChannelOperationRequestStatus.Pending || request.Status == ChannelOperationRequestStatus.PSBTSignaturesPending) &&
                                  request.ChannelOperationRequestPsbts.All(signature => signature.UserSignerId != userId))
                .Include(request => request.SourceNode)
                .Include(request => request.Wallet).ThenInclude(x => x.InternalWallet)
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(request => request.DestNode)
                .Include(request => request.ChannelOperationRequestPsbts)
                .Include(x => x.Utxos)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(ChannelOperationRequest type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            //Check for avoiding duplicate request
            var existingRequest = await applicationDbContext.ChannelOperationRequests.Where(x =>
                x.SourceNodeId == type.SourceNodeId
                && x.DestNodeId == type.DestNodeId && x.RequestType == type.RequestType).ToListAsync();

            if (existingRequest.Any(x => x.Status == ChannelOperationRequestStatus.OnChainConfirmationPending ||
                                         x.Status == ChannelOperationRequestStatus.Pending ||
                                         x.Status == ChannelOperationRequestStatus.PSBTSignaturesPending
                ))
            {
                return (false,
                    "Error, a channel operation request with the same source and destination node is in pending status, wait for that request to finalise before submitting a new request");
            }

            var valueTuple = await _repository.AddAsync(type, applicationDbContext);
            if (type.WalletId.HasValue)
            {
                await _notificationService.NotifyRequestSigners(type.WalletId.Value, "/channel-requests");
            }
            return valueTuple;
        }

        public async Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequest> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(ChannelOperationRequest type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<ChannelOperationRequest> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(ChannelOperationRequest type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            //Automapper to remove collections
            var strippedType = _mapper.Map<ChannelOperationRequest, ChannelOperationRequest>(type);
            type.SetUpdateDatetime();

            return _repository.Update(strippedType, applicationDbContext);
        }

        public async Task<(bool, string?)> AddUTXOs(ChannelOperationRequest type, List<FMUTXO> utxos)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (utxos.Count == 0) throw new ArgumentException("Value cannot be an empty collection.", nameof(utxos));

            (bool, string?) result = (true, null);

            try
            {
                await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

                var request = await applicationDbContext.ChannelOperationRequests.Include(x => x.Utxos)
                    .SingleOrDefaultAsync(x => x.Id == type.Id);
                if (request != null)
                {
                    if (!request.Utxos.Any())
                    {
                        request.Utxos = utxos;
                    }
                    else
                    {
                        request.Utxos.AddRange(utxos.Except(request.Utxos));
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

        public async Task<List<ChannelOperationRequest>> GetPendingRequests()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequests
                .Where(request => request.Status == ChannelOperationRequestStatus.Pending
                                  || request.Status == ChannelOperationRequestStatus.PSBTSignaturesPending)
                .Include(request => request.Wallet).ThenInclude(x => x.Keys)
                .Include(request => request.SourceNode)
                .Include(request => request.DestNode)
                .Include(request => request.ChannelOperationRequestPsbts)
                .Include(x => x.Utxos).AsSplitQuery()
                .ToListAsync();
        }
    }
}