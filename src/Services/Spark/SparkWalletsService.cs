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

using NBitcoin;
using NBXplorer.DerivationStrategy;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NSpark.Models;
using NSpark.Signer;

namespace NodeGuard.Services.Spark;

/// <summary>A Spark wallet as the Wallets page and the API show it.</summary>
/// <param name="MaxBalanceSats">Its own max balance; null means SPARK_MAX_BALANCE_SATS.</param>
/// <param name="EffectiveMaxBalanceSats">The max balance that applies.</param>
/// <param name="NodeIds">The nodes whose Spark swaps go through it.</param>
public sealed record SparkWalletSummary(int Id, string Name, string? IdentityPublicKey, int Account, long? MaxBalanceSats,
    long EffectiveMaxBalanceSats, IReadOnlyList<int> NodeIds, bool IsArchived, DateTimeOffset CreationDatetime);

/// <summary>A new Spark wallet and its mnemonic, which is shown once and never again.</summary>
public sealed record SparkWalletCreation(Wallet Wallet, string Mnemonic);

/// <summary>
/// Manages the Spark wallets of SPARK_SIGNER=wallet, for the Wallets page and the API. Temporary: it goes away
/// with the hot Spark wallets, once the remote signer is the only Spark signer. Every action is audited.
/// </summary>
public interface ISparkWalletsService
{
    /// <summary>Creates a Spark wallet with a new 24-word mnemonic, stored encrypted.</summary>
    /// <exception cref="InvalidOperationException">Spark is disabled, or not in SPARK_SIGNER=wallet mode.</exception>
    /// <exception cref="ArgumentException">An empty name, a negative account or a max balance below 1 sat.</exception>
    Task<SparkWalletCreation> CreateAsync(string name, int account = 0, long? maxBalanceSats = null, CancellationToken ct = default);

    /// <summary>Sets the most the wallet may hold, counting a new swap; null means SPARK_MAX_BALANCE_SATS.</summary>
    Task SetMaxBalanceAsync(int walletId, long? maxBalanceSats, CancellationToken ct = default);

    Task<IReadOnlyList<SparkWalletSummary>> ListAsync(bool includeArchived = false, CancellationToken ct = default);

    /// <exception cref="SparkUnavailableException">The wallet can't be reached.</exception>
    Task<SatsBalance> GetBalanceAsync(int walletId, CancellationToken ct = default);

    /// <summary>
    /// Exits everything the wallet holds to an unused address of an on-chain wallet, the exit fee capped by
    /// SPARK_MAX_EXIT_FEE_SATS. Refused while the wallet has a Spark swap in flight, whose sats it would take.
    /// </summary>
    Task<WithdrawAllResult> WithdrawAllAsync(int walletId, int destinationWalletId, CancellationToken ct = default);

    /// <summary>Archives the wallet; refused unless it is empty and no node uses it.</summary>
    Task ArchiveAsync(int walletId, CancellationToken ct = default);
}

public sealed class SparkWalletsService : ISparkWalletsService
{
    private readonly SparkSettings _settings;
    private readonly ISparkWalletService _spark;
    private readonly ISparkSeedProtector _protector;
    private readonly IWalletRepository _walletRepository;
    private readonly INodeRepository _nodeRepository;
    private readonly ISwapOutRepository _swapOutRepository;
    private readonly INBXplorerService _nbXplorerService;
    private readonly IAuditService _auditService;
    private readonly ILogger<SparkWalletsService> _logger;

    public SparkWalletsService(SparkSettings settings, ISparkWalletService spark, ISparkSeedProtector protector,
        IWalletRepository walletRepository, INodeRepository nodeRepository, ISwapOutRepository swapOutRepository,
        INBXplorerService nbXplorerService, IAuditService auditService, ILogger<SparkWalletsService> logger)
    {
        _settings = settings;
        _spark = spark;
        _protector = protector;
        _walletRepository = walletRepository;
        _nodeRepository = nodeRepository;
        _swapOutRepository = swapOutRepository;
        _nbXplorerService = nbXplorerService;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<SparkWalletCreation> CreateAsync(string name, int account = 0, long? maxBalanceSats = null,
        CancellationToken ct = default)
    {
        if (!_settings.Enabled || _settings.SignerMode != SparkSignerMode.Wallet)
        {
            throw new InvalidOperationException("Spark wallets are created with Spark enabled and SPARK_SIGNER=wallet.");
        }

        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A Spark wallet needs a name.", nameof(name));
        if (account < 0) throw new ArgumentException("The Spark account must be 0 or more.", nameof(account));
        ValidateMaxBalance(maxBalanceSats);

        var mnemonic = new Mnemonic(Wordlist.English, WordCount.TwentyFour).ToString();
        var identity = Convert.ToHexString(await SparkSigner.FromMnemonic(mnemonic, account).GetIdentityPublicKeyAsync())
            .ToLowerInvariant();

        var wallet = new Wallet
        {
            Name = name.Trim(),
            Kind = WalletKind.Spark,
            Description = "Spark wallet (swap-outs pass through it)",
            MofN = 1,
            IsHotWallet = true,
            IsFinalised = true,
            SparkEncryptedMnemonic = _protector.Protect(mnemonic),
            SparkAccount = account,
            SparkIdentityPublicKey = identity,
            SparkMaxBalanceSats = maxBalanceSats,
            Keys = []
        };

        var (saved, error) = await _walletRepository.AddAsync(wallet);
        if (!saved)
        {
            await _auditService.LogAsync(AuditActionType.Create, AuditEventType.Failure, AuditObjectType.Wallet, null,
                new { Name = wallet.Name, Kind = WalletKind.Spark, Error = error });
            throw new InvalidOperationException($"The Spark wallet could not be saved: {error}");
        }

        await _auditService.LogAsync(AuditActionType.Create, AuditEventType.Success, AuditObjectType.Wallet, wallet.Id.ToString(),
            new { wallet.Name, Kind = WalletKind.Spark, Account = account, IdentityPublicKey = identity, MaxBalanceSats = maxBalanceSats });
        _logger.LogInformation("Created Spark wallet {WalletId} ({Name}), identity {Identity}", wallet.Id, wallet.Name, identity);

        // Connected right away, so a key ring that can't decrypt what it just encrypted shows up now
        await _spark.EnsureReadyAsync(new SparkWalletRef(wallet.Id), ct);

        return new SparkWalletCreation(wallet, mnemonic);
    }

    public async Task SetMaxBalanceAsync(int walletId, long? maxBalanceSats, CancellationToken ct = default)
    {
        ValidateMaxBalance(maxBalanceSats);
        var wallet = await SparkWalletAsync(walletId);
        var previous = wallet.SparkMaxBalanceSats;

        wallet.SparkMaxBalanceSats = maxBalanceSats;
        var (updated, error) = _walletRepository.Update(wallet);
        await _auditService.LogAsync(AuditActionType.Update, updated ? AuditEventType.Success : AuditEventType.Failure,
            AuditObjectType.Wallet, walletId.ToString(),
            new { wallet.Name, PreviousMaxBalanceSats = previous, MaxBalanceSats = maxBalanceSats, Error = error });
        if (!updated) throw new InvalidOperationException($"The max balance could not be saved: {error}");
    }

    public async Task<IReadOnlyList<SparkWalletSummary>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var wallets = await _walletRepository.GetSparkWallets(includeArchived);
        var nodes = await _nodeRepository.GetAll();

        return wallets.Select(w => new SparkWalletSummary(w.Id, w.Name, w.SparkIdentityPublicKey, w.SparkAccount ?? 0,
                w.SparkMaxBalanceSats, _settings.MaxBalanceFor(w), nodes.Where(n => n.SparkWalletId == w.Id).Select(n => n.Id).ToList(),
                w.IsArchived, w.CreationDatetime))
            .ToList();
    }

    public async Task<SatsBalance> GetBalanceAsync(int walletId, CancellationToken ct = default)
    {
        await SparkWalletAsync(walletId);
        return (await _spark.GetBalanceAsync(new SparkWalletRef(walletId), ct)).SatsBalance;
    }

    public async Task<WithdrawAllResult> WithdrawAllAsync(int walletId, int destinationWalletId, CancellationToken ct = default)
    {
        var wallet = await SparkWalletAsync(walletId);
        var destination = await _walletRepository.GetById(destinationWalletId);
        if (destination is not { Kind: WalletKind.OnChain, IsFinalised: true, IsArchived: false, IsCompromised: false } ||
            destination.GetDerivationStrategy() is not { } strategy)
        {
            throw new InvalidOperationException($"Wallet {destinationWalletId} is not an available on-chain wallet to withdraw to.");
        }

        var sparkWallet = new SparkWalletRef(walletId);
        var details = new { SparkWallet = wallet.Name, DestinationWalletId = destinationWalletId, DestinationWallet = destination.Name };

        // Under the swaps' reservation lock: no swap of this wallet can start while its sats leave
        await SparkSwapService.Reservation.WaitAsync(ct);
        try
        {
            var inFlight = (await _swapOutRepository.GetAllPending()).FirstOrDefault(s => s.Provider == SwapProvider.Spark &&
                string.Equals(s.SparkIdentity, wallet.SparkIdentityPublicKey, StringComparison.OrdinalIgnoreCase));
            if (inFlight is not null)
            {
                throw new InvalidOperationException(
                    $"Spark swap {inFlight.Id} is in flight on {wallet.Name}: wait until it has exited, so its sats don't leave with the rest.");
            }

            await _spark.ClaimPendingAsync(sparkWallet, ct);
            var address = (await _nbXplorerService.GetUnusedAsync(strategy, DerivationFeature.Deposit, 0, true, ct)).Address.ToString();
            var exit = await _spark.WithdrawAllAsync(sparkWallet, address, _settings.MaxExitFeeSats, ct);

            await _auditService.LogAsync(AuditActionType.Transfer, AuditEventType.Success, AuditObjectType.Wallet, walletId.ToString(),
                new { details.SparkWallet, details.DestinationWalletId, details.DestinationWallet, Address = address, exit.Txid, exit.PayoutSats, exit.FeeSats });
            _logger.LogInformation("Spark wallet {WalletId} withdrew {Payout} sats to {Address} in {TxId} (SSP fee {Fee} sats)",
                walletId, exit.PayoutSats, address, exit.Txid, exit.FeeSats);
            return exit;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _auditService.LogAsync(AuditActionType.Transfer, AuditEventType.Failure, AuditObjectType.Wallet, walletId.ToString(),
                new { details.SparkWallet, details.DestinationWalletId, details.DestinationWallet, Error = e.Message });
            throw;
        }
        finally
        {
            SparkSwapService.Reservation.Release();
        }
    }

    public async Task ArchiveAsync(int walletId, CancellationToken ct = default)
    {
        var wallet = await SparkWalletAsync(walletId);

        var users = (await _nodeRepository.GetAll()).Where(n => n.SparkWalletId == walletId).Select(n => n.Name).ToList();
        if (users.Count > 0)
        {
            throw new InvalidOperationException(
                $"{wallet.Name} is the Spark wallet of {string.Join(", ", users)}: choose another in their liquidity settings first.");
        }

        var balance = await GetBalanceAsync(walletId, ct);
        if (balance.Owned + balance.Incoming > 0)
        {
            throw new InvalidOperationException(
                $"{wallet.Name} still holds {balance.Owned + balance.Incoming} sats: withdraw them first.");
        }

        wallet.IsArchived = true;
        var (updated, error) = _walletRepository.Update(wallet);
        await _auditService.LogAsync(AuditActionType.Update, updated ? AuditEventType.Success : AuditEventType.Failure,
            AuditObjectType.Wallet, walletId.ToString(), new { wallet.Name, Archived = true, Error = error });
        if (!updated) throw new InvalidOperationException($"{wallet.Name} could not be archived: {error}");

        _spark.Forget(new SparkWalletRef(walletId));
    }

    private async Task<Wallet> SparkWalletAsync(int walletId) =>
        await _walletRepository.GetById(walletId) is { Kind: WalletKind.Spark } wallet
            ? wallet
            : throw new ArgumentException($"Wallet {walletId} is not a Spark wallet.", nameof(walletId));

    private static void ValidateMaxBalance(long? maxBalanceSats)
    {
        if (maxBalanceSats is <= 0)
        {
            throw new ArgumentException("A Spark wallet's max balance must be a positive number of sats.", nameof(maxBalanceSats));
        }
    }
}
