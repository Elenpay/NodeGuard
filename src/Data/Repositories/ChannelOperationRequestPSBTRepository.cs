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
using NBitcoin;
using NodeGuard.Helpers;
using Npgsql;

namespace NodeGuard.Data.Repositories
{
    public class ChannelOperationRequestPSBTRepository : IChannelOperationRequestPSBTRepository
    {
        /// <summary>
        /// See WalletWithdrawalRequestPsbtRepository.TemplateAlreadyExists.
        /// </summary>
        public const string TemplateAlreadyExists = "A template PSBT already exists for this request.";

        private readonly IRepository<ChannelOperationRequestPSBT> _repository;
        private readonly ILogger<ChannelOperationRequestPSBTRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public ChannelOperationRequestPSBTRepository(IRepository<ChannelOperationRequestPSBT> repository,
            ILogger<ChannelOperationRequestPSBTRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<ChannelOperationRequestPSBT?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequestPSBTs.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<ChannelOperationRequestPSBT?> GetTemplateByRequestId(int channelOperationRequestId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequestPSBTs
                .SingleOrDefaultAsync(x => x.ChannelOperationRequestId == channelOperationRequestId && x.IsTemplatePSBT);
        }

        public async Task<List<ChannelOperationRequestPSBT>> GetAll()
        {
            throw new NotImplementedException();
        }

        public async Task<(bool, string?)> AddAsync(ChannelOperationRequestPSBT type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();

            //We set the request status to PSBTSignaturesPending
            var request = await
                applicationDbContext.ChannelOperationRequests
                    .Include(x => x.ChannelOperationRequestPsbts)
                    .FirstOrDefaultAsync(x => x.Id == type.ChannelOperationRequestId);

            // See WalletWithdrawalRequestPsbtRepository.ValidateApproval. Channel operations are signed with
            // SIGHASH_NONE (ChannelRequests.razor passes SigHashMode="SigHash.None").
            var validation = ValidateApproval(request, type);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Rejected PSBT for channel operation request {RequestId}: {Reason}",
                    type.ChannelOperationRequestId, validation.Error);

                return (false, validation.Error);
            }

            if (type.IsTemplatePSBT)
            {
                return await AddTemplateAsync(type, applicationDbContext);
            }

            try
            {
                if (request != null)
                {
                    request.Status = ChannelOperationRequestStatus.PSBTSignaturesPending;

                    applicationDbContext.Update(request);
                    await applicationDbContext.SaveChangesAsync();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while setting channel operation request status");
                return (false, null);
            }

            return await _repository.AddAsync(type, applicationDbContext);
        }

        /// <summary>
        /// See WalletWithdrawalRequestPsbtRepository.AddTemplateAsync: surfaces the unique-index violation
        /// (IX_ChannelOperationRequestPSBTs_Template) instead of the generic repository's (false, null).
        /// </summary>
        private async Task<(bool, string?)> AddTemplateAsync(ChannelOperationRequestPSBT type,
            ApplicationDbContext applicationDbContext)
        {
            try
            {
                applicationDbContext.Add(type);
                await applicationDbContext.SaveChangesAsync();

                return (true, null);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            })
            {
                _logger.LogWarning(
                    "Template PSBT for channel operation request {RequestId} already exists, a concurrent generation won the race",
                    type.ChannelOperationRequestId);

                return (false, TemplateAlreadyExists);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error saving template PSBT for channel operation request {RequestId}",
                    type.ChannelOperationRequestId);

                return (false, null);
            }
        }

        public async Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequestPSBT> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            // Same gate as AddAsync — it writes to the same table.
            foreach (var psbt in type)
            {
                var request = await applicationDbContext.ChannelOperationRequests
                    .Include(x => x.ChannelOperationRequestPsbts)
                    .FirstOrDefaultAsync(x => x.Id == psbt.ChannelOperationRequestId);

                var validation = ValidateApproval(request, psbt);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("Rejected PSBT for channel operation request {RequestId}: {Reason}",
                        psbt.ChannelOperationRequestId, validation.Error);

                    return (false, validation.Error);
                }
            }

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(ChannelOperationRequestPSBT type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<ChannelOperationRequestPSBT> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        /// <summary>
        /// Validates a human approval against the request's template PSBT. Server-generated rows (template,
        /// internal wallet signature, finalised PSBT) pass through.
        /// </summary>
        private static PsbtApprovalValidator.Result ValidateApproval(ChannelOperationRequest? request,
            ChannelOperationRequestPSBT type)
        {
            if (type.IsTemplatePSBT || type.IsInternalWalletPSBT || type.IsFinalisedPSBT)
            {
                return PsbtApprovalValidator.Result.Ok;
            }

            if (request == null)
            {
                return PsbtApprovalValidator.Result.Fail("The channel operation request could not be found.");
            }

            var existing = request.ChannelOperationRequestPsbts?
                .Where(x => !x.IsTemplatePSBT && !x.IsInternalWalletPSBT && !x.IsFinalisedPSBT)
                .Select(x => x.PSBT)
                .ToList() ?? new List<string>();

            string? template;
            try
            {
                template = request.GetSingleTemplatePsbt()?.PSBT;
            }
            catch (InvalidOperationException e)
            {
                return PsbtApprovalValidator.Result.Fail(e.Message);
            }

            if (string.IsNullOrWhiteSpace(template))
            {
                return PsbtApprovalValidator.Result.Fail(
                    "This request has no template PSBT to validate the signature against.");
            }

            return PsbtApprovalValidator.Validate(template, type.PSBT, SigHash.None,
                CurrentNetworkHelper.GetCurrentNetwork(), existing);
        }

        public (bool, string?) Update(ChannelOperationRequestPSBT type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetUpdateDatetime();

            return _repository.Update(type, applicationDbContext);
        }
    }
}