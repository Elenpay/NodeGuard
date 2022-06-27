using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ChannelOperationRequestRepository : IChannelOperationRequestRepository
    {
        private readonly IRepository<ChannelOperationRequest> _repository;
        private readonly ILogger<ChannelOperationRequestRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public ChannelOperationRequestRepository(IRepository<ChannelOperationRequest> repository,
            ILogger<ChannelOperationRequestRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<ChannelOperationRequest?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequests.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<ChannelOperationRequest>> GetAll()
        {
            return new List<ChannelOperationRequest>() { new ChannelOperationRequest()
            {
                Id = 1,
                Description = "first",
                RequestType = OperationRequestType.Open,
                SourceNodeId = 11,
                DestNodeId = 12,
                Amount = 121,
                AmountCryptoUnit = "Sat",
                Status = ChannelOperationRequestStatus.Approved,
                UserId = "99111",
                ChannelId = 112,
                CreationDatetime = DateTimeOffset.Now,
                UpdateDatetime = DateTimeOffset.Now,
                DestNode = new Node()
                {
                    ChannelAdminMacaroon = "Macaroon",
                    ChannelOperationRequestsAsDestination = new List<ChannelOperationRequest>(),
                    ChannelOperationRequestsAsSource = new List<ChannelOperationRequest>(),
                    CreationDatetime = new DateTimeOffset(2022, 7, 1, 8, 6, 32, new TimeSpan(1, 0, 0)),
                    UpdateDatetime = new DateTimeOffset(2022, 7, 2, 9, 7, 16, new TimeSpan(1, 0, 0)),
                    Description = "Bitrefill",
                    Endpoint = "test.com:1234",
                    Name = "Bitrefill"
                },
                Wallet = new Wallet()
                {
                    Name = "Clovr Labs Wallet"
                },
                ChannelOperationRequestSignatures = new List<ChannelOperationRequestSignature>()
                {
                    new ChannelOperationRequestSignature()
                    {
                        PSBT = "PSBT123",
                        ChannelOpenRequestId = 1,

                        CreationDatetime = DateTimeOffset.Now,
                        UpdateDatetime = DateTimeOffset.Now,
                        SignatureContent = "aslkjsalkjflafjalksjklajdla"
                    }
                }

            },
            new ChannelOperationRequest()
            {
                Id = 2,
                Description = "second",
                RequestType = OperationRequestType.Open,
                SourceNodeId = 11,
                DestNodeId = 32,
                Amount = 5234,
                AmountCryptoUnit = "Sat",
                Status = ChannelOperationRequestStatus.Pending,
                UserId = "88543",
                ChannelId = 2221,
                CreationDatetime = new DateTimeOffset(2020, 5, 1, 8, 6, 32, new TimeSpan(1, 0, 0)),
                UpdateDatetime = new DateTimeOffset(2020, 5, 2, 9, 7, 16, new TimeSpan(1, 0, 0)),
                DestNode = new Node()
                {
                    ChannelAdminMacaroon = "Macaroon",
                    ChannelOperationRequestsAsDestination = new List<ChannelOperationRequest>(),
                    ChannelOperationRequestsAsSource = new List<ChannelOperationRequest>(),
                    CreationDatetime = new DateTimeOffset(2022, 7, 1, 8, 6, 32, new TimeSpan(1, 0, 0)),
                    UpdateDatetime = new DateTimeOffset(2022, 7, 2, 9, 7, 16, new TimeSpan(1, 0, 0)),
                    Description = "BCash_is_Trash",
                    Endpoint = "test.com:1234",
                    Name = "BCash_is_Trash"
                },
                Wallet = new Wallet()
                {
                    Name = "My Personal Wallet"
                },
                ChannelOperationRequestSignatures = new List<ChannelOperationRequestSignature>()
                {
                    new ChannelOperationRequestSignature()
                    {
                        PSBT = "PSBT789",
                        ChannelOpenRequestId = 1,

                        CreationDatetime = DateTimeOffset.Now,
                        UpdateDatetime = DateTimeOffset.Now,
                        SignatureContent = "kjehfkjhnjekrhfjheruhuiewfuiheiuhf"
                    }
                }
            },
            new ChannelOperationRequest()
            {
                Id = 3,
                Description = "Third",
                RequestType = OperationRequestType.Close,
                SourceNodeId = 31,
                DestNodeId = 32,
                Amount = 2,
                AmountCryptoUnit = "Sat",
                Status = ChannelOperationRequestStatus.Pending,
                UserId = "234456",
                ChannelId = 1234,
                CreationDatetime = new DateTimeOffset(2022, 7, 1, 8, 6, 32, new TimeSpan(1, 0, 0)),
                UpdateDatetime = new DateTimeOffset(2022, 7, 2, 9, 7, 16, new TimeSpan(1, 0, 0)),
                DestNode = new Node()
                {
                    ChannelAdminMacaroon = "Macaroon",
                    ChannelOperationRequestsAsDestination = new List<ChannelOperationRequest>(),
                    ChannelOperationRequestsAsSource = new List<ChannelOperationRequest>(),
                    CreationDatetime = new DateTimeOffset(2022, 7, 1, 8, 6, 32, new TimeSpan(1, 0, 0)),
                    UpdateDatetime = new DateTimeOffset(2022, 7, 2, 9, 7, 16, new TimeSpan(1, 0, 0)),
                    Description = "LNBIG",
                    Endpoint = "test.com:1234",
                    Name = "LNBIG"
                },
                Wallet = new Wallet()
                {
                    Name = "Botin's Family crypto Wallet"
                },
                ChannelOperationRequestSignatures = new List<ChannelOperationRequestSignature>()
                {
                    new ChannelOperationRequestSignature()
                    {
                        PSBT = "PSBT123",
                        ChannelOpenRequestId = 1,

                        CreationDatetime = DateTimeOffset.Now,
                        UpdateDatetime = DateTimeOffset.Now,
                        SignatureContent = "aslkjsalkjflafjalksjklajdla"
                    },
                    new ChannelOperationRequestSignature()
                    {
                        PSBT = "PSBT456",
                        ChannelOpenRequestId = 2,

                        CreationDatetime = DateTimeOffset.Now,
                        UpdateDatetime = DateTimeOffset.Now,
                        SignatureContent = "poweirpowiefposidfpodifopi"
                    }
                }
            }};
        }

        public async Task<(bool, string?)> AddAsync(ChannelOperationRequest type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddAsync(type, applicationDbContext);
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

            return _repository.Update(type, applicationDbContext);
        }
    }
}

