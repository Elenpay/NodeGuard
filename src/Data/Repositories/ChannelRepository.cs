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

﻿using AutoMapper;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Jobs;
using FundsManager.Helpers;
using Quartz;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ChannelRepository : IChannelRepository
    {
        private readonly IRepository<Channel> _repository;
        private readonly ILogger<ChannelRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
        private readonly ISchedulerFactory _schedulerFactory;
        private readonly IMapper _mapper;

        public ChannelRepository(IRepository<Channel> repository,
            ILogger<ChannelRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IChannelOperationRequestRepository channelOperationRequestRepository, ISchedulerFactory schedulerFactory,
            IMapper mapper
            )
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _channelOperationRequestRepository = channelOperationRequestRepository;
            _schedulerFactory = schedulerFactory;
            this._mapper = mapper;
        }

        public async Task<Channel?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Channels.Include(x => x.ChannelOperationRequests).FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<Channel>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Channels
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.User)
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.SourceNode)
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.Wallet)
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.DestNode)
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.ChannelOperationRequestPsbts)
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(Channel type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<Channel> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(Channel type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public async Task<(bool, string?)> SafeRemove(Channel type, bool forceClose = false)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
            var openRequest = applicationDbContext.ChannelOperationRequests.Include(x => x.SourceNode).SingleOrDefault(request => request.ChannelId == type.Id && request.RequestType == OperationRequestType.Open);
            var closeRequest = new ChannelOperationRequest
            {
                ChannelId = type.Id,
                RequestType = OperationRequestType.Close,
                CreationDatetime = DateTimeOffset.Now,
                UpdateDatetime = DateTimeOffset.Now,
                Status = ChannelOperationRequestStatus.Approved, // Close operations are pre-approved by default
                Description = "Close Channel Operation for Channel " + type.Id,
            };
            if (openRequest != null)
            {
                closeRequest.WalletId = openRequest.WalletId;
                closeRequest.SourceNodeId = openRequest.SourceNodeId;

                closeRequest.DestNodeId = openRequest.DestNodeId;
                closeRequest.AmountCryptoUnit = openRequest.AmountCryptoUnit;
                closeRequest.SatsAmount = openRequest.SatsAmount;
                closeRequest.UserId = openRequest.UserId;
            }
            else
            {
                _logger.LogWarning("Could not find a opening request operation for this channel Some of the fields will be not set");
            }

            var closeRequestAddResult = await _channelOperationRequestRepository.AddAsync(closeRequest);

            if (!closeRequestAddResult.Item1)
            {
                _logger.LogError("Error while saving close request for channel with id: {RequestId}", type.Id);
                return (false, closeRequestAddResult.Item2);
            }

            closeRequest = await _channelOperationRequestRepository.GetById(closeRequest.Id);

            if (closeRequest == null) return (false, null);

            var scheduler = await _schedulerFactory.GetScheduler(); 
            
            var map = new JobDataMap();
            map.Put("closeRequestId", closeRequest.Id);
            map.Put("forceClose", forceClose);

            var retryList = RetriableJob.ParseRetryListFromString(Constants.JOB_RETRY_INTERVAL_LIST_IN_MINUTES);
            var job = RetriableJob.Create<ChannelCloseJob>(map, closeRequest.Id.ToString(), retryList);
            await scheduler.ScheduleJob(job.Job, job.Trigger);

            // TODO: Check job id
            closeRequest.JobId = job.Job.Key.ToString();

            var jobUpdateResult = _channelOperationRequestRepository.Update(closeRequest);
            if (!jobUpdateResult.Item1)
            {
                _logger.LogError("Error while updating the JobId for the close request with id: {RequestId}",
                    closeRequest.Id);
            }

            return (true, null);
        }

        public (bool, string?) RemoveRange(List<Channel> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(Channel type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            //Automapper to avoid creation of entities
            type = _mapper.Map<Channel, Channel>(type);

            return _repository.Update(type, applicationDbContext);
        }
    }
}