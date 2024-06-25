using AutoMapper;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Quartz;
using ILogger = Microsoft.Build.Framework.ILogger;

namespace NodeGuard.Jobs;

public class RegisterUTXOsJob : IJob
{
    private readonly ILogger<RegisterUTXOsJob> _logger;
    private readonly INBXplorerService _nbXplorerService;
    private readonly IWalletRepository _walletRepository;
    private readonly IFMUTXORepository _fmutxoRepository;
    private readonly IMapper _mapper;
    
    public RegisterUTXOsJob(ILogger<RegisterUTXOsJob> logger, INBXplorerService nbXplorerService,
        IWalletRepository walletRepository, IFMUTXORepository fmutxoRepository, IMapper mapper)
    {
        _logger = logger;
        _nbXplorerService = nbXplorerService;
        _walletRepository = walletRepository;
        _fmutxoRepository = fmutxoRepository;
        _mapper = mapper;
    }
    public async Task Execute(IJobExecutionContext context)
    {
        var wallets = await _walletRepository.GetAll();
        foreach (var wallet in wallets)
        {
            var utxos = await _nbXplorerService.GetUTXOsAsync(wallet.GetDerivationStrategy());
            utxos.RemoveDuplicateUTXOs();

            var existingUtxos = await _fmutxoRepository.GetByWalletId(wallet.Id);

            var newUtxos = utxos.GetUnspentUTXOs()
                .Where(x => !existingUtxos.Any(y => y.Equals(x))).ToList();

            if (newUtxos.Any())
            {
                var fmUtxos = newUtxos.Select(x => _mapper.Map<UTXO, FMUTXO>(x)).ToList();
                foreach (var fmUtxo in fmUtxos)
                {
                    fmUtxo.WalletId = wallet.Id;
                    fmUtxo.SetCreationDatetime();
                    fmUtxo.SetUpdateDatetime();
                }
                var (success, message) = await _fmutxoRepository.AddRangeAsync(fmUtxos);
                if (!success)
                {
                    _logger.LogError("Error adding UTXOs to database for wallet {WalletId}: {Message}", wallet.Id, message);
                }
            }
            else
            {
                _logger.LogInformation("No new UTXOs found for wallet {WalletId}", wallet.Id);
            }
        }
    }
}