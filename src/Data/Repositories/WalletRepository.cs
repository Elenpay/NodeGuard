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

using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using FundsManager.Services;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Nodeguard;
using Key = FundsManager.Data.Models.Key;
using Wallet = FundsManager.Data.Models.Wallet;

namespace FundsManager.Data.Repositories
{
    public class WalletRepository : IWalletRepository
    {
        private readonly IRepository<Wallet> _repository;
        private readonly ILogger<WalletRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IInternalWalletRepository _internalWalletRepository;
        private readonly IKeyRepository _keyRepository;
        private readonly INBXplorerService _nbXplorerService;

        public WalletRepository(IRepository<Wallet> repository,
            ILogger<WalletRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IInternalWalletRepository internalWalletRepository, IKeyRepository keyRepository,
            INBXplorerService nbXplorerService
        )
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _internalWalletRepository = internalWalletRepository;
            _keyRepository = keyRepository;
            _nbXplorerService = nbXplorerService;
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

        public async Task<List<Wallet>> GetAvailableByType(WALLET_TYPE type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Wallets
                .Where(w =>
                    !w.IsArchived && !w.IsCompromised && w.IsFinalised &&
                    (type == WALLET_TYPE.Both || type == WALLET_TYPE.Cold && !w.IsHotWallet ||
                     type == WALLET_TYPE.Hot && w.IsHotWallet))
                .Include(x => x.InternalWallet)
                .Include(x => x.Keys)
                .ToListAsync();
        }

        public async Task<List<Wallet>> GetAvailableByIds(List<int> ids)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Wallets
                .Where(w => !w.IsArchived && !w.IsCompromised && w.IsFinalised && ids.Contains(w.Id))
                .Include(x => x.InternalWallet)
                .Include(x => x.Keys)
                .ToListAsync();
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

            
            if (type.IsBIP39Imported)
            {
                //Persist
                
                var addResult = await _repository.AddAsync(type, applicationDbContext);

                return addResult;
            }

            try
            {

                //We add the internal wallet of the moment and its key if it is not a BIP39 wallet
                
                var currentInternalWallet = (await _internalWalletRepository.GetCurrentInternalWallet());
                if (currentInternalWallet != null)
                {
                    type.InternalWalletId = currentInternalWallet.Id;
                    type.InternalWalletMasterFingerprint = currentInternalWallet.MasterFingerprint;
                }

                type.InternalWalletSubDerivationPath = await GetNextSubderivationPath();
                var currentInternalWalletKey =
                    await _keyRepository.GetCurrentInternalWalletKey(type.InternalWalletSubDerivationPath);

                type.Keys = new List<Key>();

                var addResult = await _repository.AddAsync(type, applicationDbContext);
                
                if (!addResult.Item1)
                    return addResult;

                if (currentInternalWalletKey != null)
                    type.Keys.Add(currentInternalWalletKey);

                //We need to persist before updating a m-of-n relationship
                applicationDbContext.Update(type);
                await applicationDbContext.SaveChangesAsync();

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

        public (bool, string?) Update(Wallet type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();
            var wallet = applicationDbContext.Wallets.Include(w => w.Keys).FirstOrDefault(x => x.Id == type.Id);

            type.SetUpdateDatetime();

            var wasHotWallet = wallet.IsHotWallet;
            applicationDbContext.Entry(wallet).CurrentValues.SetValues(type);

            if (!wasHotWallet && type.IsHotWallet)
            {
                var userKeys = wallet.Keys.Where(k => !string.IsNullOrEmpty(k.UserId));
                foreach (var key in userKeys)
                {
                    wallet.Keys.Remove(key);
                }
            }
            else
            {
                foreach (var key in type.Keys)
                {
                    if (!Key.Contains(wallet.Keys, key))
                    {
                        wallet.Keys.Add(key);
                    }
                }
            }

            wallet.ChannelOperationRequestsAsSource?.Clear();
            return _repository.Update(wallet, applicationDbContext);
        }

        public async Task<string> GetNextSubderivationPath()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var lastWallet = applicationDbContext.Wallets.OrderBy(w => w.Id).LastOrDefault(w => w.IsFinalised);

            if (lastWallet == null || string.IsNullOrEmpty(lastWallet.InternalWalletSubDerivationPath)) return "0";

            var subderivationPath = KeyPath.Parse(lastWallet.InternalWalletSubDerivationPath);
            return subderivationPath.Increment().ToString();
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
                var derivationStrategyBase = selectedWalletToFinalise.GetDerivationStrategy();
                if (derivationStrategyBase == null)
                {
                    return (false, "Error while getting the derivation scheme");
                }

                await _nbXplorerService.TrackAsync(derivationStrategyBase, default);

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

        public async Task<(bool, string?)> ImportBIP39Wallet(string name, string description, string seedphrase, string derivationPath,
            string? userId = null)
        {
            if (string.IsNullOrWhiteSpace(seedphrase))
                return (false, "Seedphrase is empty");

            if (string.IsNullOrWhiteSpace(derivationPath))
                return (false, "Derivation path is empty");

            try
            {
                
                //Validate derivation path
                var keyPath = KeyPath.Parse(derivationPath);
                
                //Mnenomic create
                var mnemonic = new Mnemonic(seedphrase);

                if (mnemonic == null)
                {
                    _logger.LogError("Seedphrase is invalid");
                    return (false, "Seedphrase is invalid");
                }

                var currentNetwork = CurrentNetworkHelper.GetCurrentNetwork();
                //Get xpub and fingerprint
                var extKey = mnemonic.DeriveExtKey().Derive(new KeyPath(derivationPath));
                var xpub = extKey.Neuter().GetWif(currentNetwork).ToString();
                var masterFingerprint = extKey.GetWif(currentNetwork).GetPublicKey()
                    .GetHDFingerPrint().ToString();

                var wallet = new Wallet
                {
                    CreationDatetime = DateTimeOffset.Now,
                    UpdateDatetime = DateTimeOffset.Now,
                    Name = name,
                    MofN = 1,
                    Description = description,
                    IsArchived = false,
                    IsCompromised = false,
                    IsFinalised = true,
                    WalletAddressType = WalletAddressType.NativeSegwit,
                    IsHotWallet = true, //For now, imported wallet are hot wallet that do not require user interaction
                    IsBIP39Imported = true,
                    BIP39Seedphrase = Constants.ENABLE_REMOTE_SIGNER ? null : seedphrase,
                    Keys = new List<Key>()
                };

                //Persist wallet
                var addResult = await AddAsync(wallet);
                if (addResult.Item1 == false)
                {
                    _logger.LogError("Error while importing wallet from seedphrase: {Error}", addResult.Item2);
                    return (false, addResult.Item2);
                }

                //Create key 
                var key = new Key
                {
                    CreationDatetime = DateTimeOffset.Now,
                    UpdateDatetime = DateTimeOffset.Now,
                    Name = $"{masterFingerprint} key",
                    XPUB = xpub,
                    Description = "Imported BIP39 wallet key",
                    IsArchived = false,
                    IsCompromised = false,
                    MasterFingerprint = masterFingerprint,
                    Path = keyPath.ToString(),
                    UserId = userId,
                    IsBIP39ImportedKey = true,
                };

                //Persist key
                var addKeyResult = await _keyRepository.AddAsync(key);
                if (addKeyResult.Item1 == false)
                {
                    _logger.LogError("Error while importing wallet from seedphrase, invalid key: {Error}",
                        addKeyResult.Item2);
                    return (false, addKeyResult.Item2);
                }

                //Add key to wallet
                wallet.Keys.Add(key);

                //Update wallet
                var updateResult = Update(wallet);
                if (updateResult.Item1 == false)
                {
                    _logger.LogError("Error while importing wallet from seedphrase, invalid wallet: {Error}",
                        updateResult.Item2);
                    return (false, updateResult.Item2);
                }

                var derivationStrategyBase = wallet.GetDerivationStrategy();
                if (derivationStrategyBase == null)
                {
                    return (false, "Error while getting the derivation scheme");
                }

                //Track wallet
                await _nbXplorerService.TrackAsync(derivationStrategyBase, default);

                //Since already existing wallet's utxos are not tracked by NBXplorer, we need to rescan the UTXO set for this wallet
                //This is a long running operation in nbxplorer and should be queried in the background
                await _nbXplorerService.ScanUTXOSetAsync(derivationStrategyBase, 1000, 30000, null, default);

               
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while importing wallet from seedphrase");
                return (false, "Error while importing wallet from seedphrase");
            }
            return (true, null);
        }
    }
}