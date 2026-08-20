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
using NBitcoin;
using NodeGuard.Helpers;

namespace NodeGuard.Data.Repositories
{
    public class WalletWithdrawalRequestPsbtRepository : IWalletWithdrawalRequestPsbtRepository
    {
        private readonly IRepository<WalletWithdrawalRequestPSBT> _repository;
        private readonly ILogger<WalletWithdrawalRequestPsbtRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public WalletWithdrawalRequestPsbtRepository(IRepository<WalletWithdrawalRequestPSBT> repository,
            ILogger<WalletWithdrawalRequestPsbtRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<WalletWithdrawalRequestPSBT?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequestPSBTs.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<WalletWithdrawalRequestPSBT>> GetAll()
        {
            throw new NotImplementedException();
        }

        public async Task<(bool, string?)> AddAsync(WalletWithdrawalRequestPSBT type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();

            //We set the request status to PSBTSignaturesPending
            var request = await
                applicationDbContext.WalletWithdrawalRequests
                    .Include(x => x.WalletWithdrawalRequestPSBTs)
                    .FirstOrDefaultAsync(x => x.Id == type.WalletWithdrawalRequestId);

            // An approval is untrusted input: the approver pastes base64 they produced offline. It must be
            // proven to describe the transaction this request was actually raised for BEFORE it is stored and
            // counted toward the signature threshold. Without this, a keyholder can substitute a transaction
            // paying themselves and have NodeGuard co-sign it.
            var validation = ValidateApproval(request, type);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Rejected PSBT for withdrawal request {RequestId}: {Reason}",
                    type.WalletWithdrawalRequestId, validation.Error);

                return (false, validation.Error);
            }

            try
            {
                if (request != null && !type.IsTemplatePSBT )
                {
                    request.Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending;

                    applicationDbContext.Update(request);
                    await applicationDbContext.SaveChangesAsync();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Erro while setting withdrawal request status");
                return (false, null);
            }

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<WalletWithdrawalRequestPSBT> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            // Currently unused, but it writes to the same table, so it gets the same gate rather than being
            // left as a way to store an unvalidated approval later on.
            foreach (var psbt in type)
            {
                var request = await applicationDbContext.WalletWithdrawalRequests
                    .Include(x => x.WalletWithdrawalRequestPSBTs)
                    .FirstOrDefaultAsync(x => x.Id == psbt.WalletWithdrawalRequestId);

                var validation = ValidateApproval(request, psbt);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("Rejected PSBT for withdrawal request {RequestId}: {Reason}",
                        psbt.WalletWithdrawalRequestId, validation.Error);

                    return (false, validation.Error);
                }
            }

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        /// <summary>
        /// Validates a human approval against the request's template PSBT. Rows that are not human approvals —
        /// the template itself, NodeGuard's own internal-wallet signature, and the finalised PSBT — are
        /// generated server side rather than submitted, so they pass through.
        /// </summary>
        private static PsbtApprovalValidator.Result ValidateApproval(WalletWithdrawalRequest? request,
            WalletWithdrawalRequestPSBT type)
        {
            if (type.IsTemplatePSBT || type.IsInternalWalletPSBT || type.IsFinalisedPSBT)
            {
                return PsbtApprovalValidator.Result.Ok;
            }

            if (request == null)
            {
                return PsbtApprovalValidator.Result.Fail("The withdrawal request could not be found.");
            }

            var existing = request.WalletWithdrawalRequestPSBTs?
                .Where(x => !x.IsTemplatePSBT && !x.IsInternalWalletPSBT && !x.IsFinalisedPSBT)
                .Select(x => x.PSBT)
                .ToList() ?? new List<string>();

            var template = request.WalletWithdrawalRequestPSBTs?
                .FirstOrDefault(x => x.IsTemplatePSBT)?.PSBT;

            if (string.IsNullOrWhiteSpace(template))
            {
                return PsbtApprovalValidator.Result.Fail(
                    "This request has no template PSBT to validate the signature against.");
            }

            return PsbtApprovalValidator.Validate(template, type.PSBT, SigHash.All,
                CurrentNetworkHelper.GetCurrentNetwork(), existing);
        }

        public (bool, string?) Remove(WalletWithdrawalRequestPSBT type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<WalletWithdrawalRequestPSBT> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(WalletWithdrawalRequestPSBT type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetUpdateDatetime();

            return _repository.Update(type, applicationDbContext);
        }
    }
}