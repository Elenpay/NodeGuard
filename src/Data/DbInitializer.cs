using FundsManager.Data.Models;
using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data
{
    public static class DbInitializer
    {
        public static void Initialize(IServiceProvider serviceProvider)
        {
            //DI
            var applicationDbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();
            var appUserRepository = serviceProvider.GetRequiredService<IApplicationUserRepository>();
            var nodeRepository = serviceProvider.GetRequiredService<INodeRepository>();
            var channelOperationRequestRepository = serviceProvider.GetRequiredService<IChannelOperationRequestRepository>();
            var walletRepository = serviceProvider.GetRequiredService<IWalletRepository>();
            var keyRepository = serviceProvider.GetRequiredService<IKeyRepository>();

            var webHostEnvironment = serviceProvider.GetRequiredService<IWebHostEnvironment>();


            //Migrations
            var isConnected = false;
            while (isConnected == false)
            {
                try
                {
                    applicationDbContext.Database.Migrate();
                    isConnected = true;
                }
                catch
                {
                }
                Thread.Sleep(1_000);

            }

            //Roles
            SetRoles(applicationDbContext);

            if (webHostEnvironment.IsDevelopment())
            {
                var notTheBoss = new ApplicationUser()
                {
                    AccessFailedCount = 0,
                    Email = "userinalice@elenpay.com",
                    EmailConfirmed = true,
                    LockoutEnabled = false,
                    UserName = "userinalice",
                    ChannelOperationRequests = new List<ChannelOperationRequest>()
                };

                var theBoss = new ApplicationUser()
                {
                    AccessFailedCount = 0,
                    Email = "boss@blockchain.com",
                    EmailConfirmed = true,
                    LockoutEnabled = false,
                    UserName = "boss",
                    ChannelOperationRequests = new List<ChannelOperationRequest>()
                };
                
                var someoneElse = new ApplicationUser()
                {
                    AccessFailedCount = 0,
                    Email = "nobody@blockchain.com",
                    EmailConfirmed = true,
                    LockoutEnabled = false,
                    UserName = "nobody",
                    ChannelOperationRequests = new List<ChannelOperationRequest>()
                };
                
                // Users
                var users = Task.Run(()=>appUserRepository.GetAll()).Result;
                if (!users.Any())
                {
                    _ = Task.Run(() => appUserRepository.AddAsync(theBoss)).Result;
                    _ = Task.Run(() => appUserRepository.AddAsync(someoneElse)).Result;
                    // TODO replace with AddRangeSync once we get it working
                }

                // nodes
                //Testing node from Polar (ALICE) LND 0.14.3 -> check devnetwork.zip polar file
                var nodes = Task.Run(()=>nodeRepository.GetAll()).Result;
                var alice = new Node
                {
                    ChannelAdminMacaroon =
                        "0201036c6e6402f801030a108be5b2928f746a822b04a9b2848eb0321201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e6572617465120472656164000006208e8b02d4bc0efd4f15a52946c5ef23f2954f8a07ed800733554a11a190cb71b4",
                    //THIS MIGHT CHANGE ON YOUR MACHINE!!
                    Endpoint = "host.docker.internal:10001",
                    Name = "Alice",
                    CreationDatetime = DateTimeOffset.UtcNow,
                    PubKey = "0201036c6e6402f801030a10dc64226b045d25f090b114baebcbf04c1201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e657261746512047265616400000620a21b8cc8c071aa5104b706b751aede972f642537c05da31450fb4b02c6da776e",
                    Users = new List<ApplicationUser>(){notTheBoss}
                };
                if (!nodes.Any(x => x.PubKey == alice.PubKey))
                {
                    _ = Task.Run(() => nodeRepository.AddAsync(alice)).Result;
                }

                //Testing node from Polar (CAROL) LND 0.14.3 -> check devnetwork.zip polar file
                var carol = new Node
                {
                    ChannelAdminMacaroon = "0201036c6e6402f801030a10dc64226b045d25f090b114baebcbf04c1201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e657261746512047265616400000620a21b8cc8c071aa5104b706b751aede972f642537c05da31450fb4b02c6da776e",
                    //THIS MIGHT CHANGE ON YOUR MACHINE!!
                    Endpoint = "host.docker.internal:10003",
                    Name = "Carol",
                    CreationDatetime = DateTimeOffset.UtcNow,
                    PubKey = "03485d8dcdd149c87553eeb80586eb2bece874d412e9f117304446ce189955d375",
                };
                
                if (!nodes.Any(x => x.PubKey == carol.PubKey))
                {
                    _ = Task.Run(() => nodeRepository.AddAsync(carol)).Result;
                }
                
                //Testing node from Polar (CAROL) LND 0.14.3 -> check devnetwork.zip polar file
                var bob = new Node
                {
                    ChannelAdminMacaroon = "04c1201301a160a0761646472657373120472656164120577726974651a130a046e",
                    //THIS MIGHT CHANGE ON YOUR MACHINE!!
                    Endpoint = "host.docker.internal:10005",
                    Name = "bob",
                    CreationDatetime = DateTimeOffset.UtcNow,
                    PubKey = "4d412e9f117304446ce189955d37503485d8dcdd149c87553eeb80586eb2bece87",
                    
                };
                
                if (!nodes.Any(x => x.PubKey == bob.PubKey))
                {
                    _ = Task.Run(() => nodeRepository.AddAsync(bob)).Result;
                }

                // Keys
                Key secretKey = new Key()
                {
                    Description = "Master key do not share",
                    Name = "Secret",
                    IsArchived = false,
                    IsCompromised = false,
                    XPUB = "test",
                    UserId = theBoss.Id,
                    Wallets = new List<Wallet>()
                };
                Key superSecretKey = new Key()
                {
                    Description = "Master key do not share, please", 
                    Name = "SuperSecret",
                    IsArchived = false,
                    IsCompromised = false,
                    XPUB = "test",
                    UserId = notTheBoss.Id,
                    Wallets = new List<Wallet>()
                };
                Key topSecretKey = new Key()
                {
                    Description = "Master key do not share, please please",
                    Name = "TopSecret",
                    IsArchived = false,
                    IsCompromised = false,
                    XPUB = "test",
                    UserId = someoneElse.Id,
                    Wallets = new List<Wallet>()
                };
                
                var keys = Task.Run(()=>keyRepository.GetAll()).Result;
                if (!keys.Any())
                {
                    _ = Task.Run(() => keyRepository.AddAsync(secretKey)).Result;
                    _ = Task.Run(() => keyRepository.AddAsync(superSecretKey)).Result;
                    _ = Task.Run(() => keyRepository.AddAsync(topSecretKey)).Result;
                    // TODO replace with AddRangeSync once we get it working
                }
                
                // Wallets
                var wallets = Task.Run(()=>walletRepository.GetAll()).Result;
                Wallet myWallet = new Wallet()
                {
                    Name = "My Personal Wallet",
                    IsArchived = false,
                    IsCompromised = false,
                    CreationDatetime = DateTimeOffset.UtcNow,
                    UpdateDatetime = DateTimeOffset.UtcNow,
                    MofN = 7,
                    Description = "Random wallet created for development"
                };
                Wallet someWallet = new Wallet()
                {
                    Name = "Botin Family crypto Wallet",
                    IsArchived = false,
                    IsCompromised = false,
                    CreationDatetime = new DateTimeOffset(2022, 7, 1, 8, 6, 32, new TimeSpan(0, 0, 0)),
                    UpdateDatetime = DateTimeOffset.UtcNow,
                    MofN = 3,
                    Description = "Random wallet created for development"
                };
                Wallet clovrWallet = new Wallet()
                {
                    Name = "Clovr Labs Wallet",
                    IsArchived = false,
                    IsCompromised = false,
                    CreationDatetime = new DateTimeOffset(2022, 7, 1, 8, 6, 32, new TimeSpan(0, 0, 0)),
                    UpdateDatetime = DateTimeOffset.UtcNow,
                    MofN = 3,
                    Description = "Random wallet created for development"
                };
                if (!wallets.Any())
                {
                    superSecretKey.Wallets.Add(myWallet);
                    secretKey.Wallets.Add(someWallet);
                    topSecretKey.Wallets.Add(clovrWallet);
                    _ = Task.Run(() => keyRepository.Update(topSecretKey)).Result;
                    _ = Task.Run(() => keyRepository.Update(secretKey)).Result;
                    _ = Task.Run(() => keyRepository.Update(superSecretKey)).Result;
                    // TODO replace with AddRangeSync once we get it working
                }
            
                // ChannelOperationRequests
                var operationRequests = Task.Run(()=>channelOperationRequestRepository.GetAll()).Result;
                if (!operationRequests.Any())
                {
                    ChannelOperationRequest firstOpRequest = new()
                    {
                        Description = "first",
                        RequestType = OperationRequestType.Open,
                        SourceNodeId = alice.Id,
                        DestNodeId = carol.Id,
                        Amount = 121,
                        AmountCryptoUnit = "Sat",
                        Status = ChannelOperationRequestStatus.Approved,
                        CreationDatetime = DateTimeOffset.Now,
                        UpdateDatetime = DateTimeOffset.Now,
                        WalletId = myWallet.Id,
                        ChannelOperationRequestSignatures = new List<ChannelOperationRequestSignature>()
                        {
                            new()
                            {
                                PSBT = "PSBT123",
                                ChannelOpenRequestId = 1,

                                CreationDatetime = DateTimeOffset.Now,
                                UpdateDatetime = DateTimeOffset.Now,
                                SignatureContent = "aslkjsalkjflafjalksjklajdla"
                            }
                        }
                    };
                    ChannelOperationRequest secondOpRequest = new()
                    {
                        Description = "second",
                        RequestType = OperationRequestType.Open,
                        SourceNodeId = alice.Id,
                        DestNodeId = carol.Id,
                        Amount = 5234,
                        AmountCryptoUnit = "Sat",
                        Status = ChannelOperationRequestStatus.Pending,
                        CreationDatetime = DateTimeOffset.UtcNow,
                        UpdateDatetime = DateTimeOffset.UtcNow,
                        WalletId = someWallet.Id,
                        ChannelOperationRequestSignatures = new List<ChannelOperationRequestSignature>()
                        {
                            new()
                            {
                                PSBT = "PSBT789",
                                ChannelOpenRequestId = 1,

                                CreationDatetime = DateTimeOffset.Now,
                                UpdateDatetime = DateTimeOffset.Now,
                                SignatureContent = "kjehfkjhnjekrhfjheruhuiewfuiheiuhf"
                            }
                        }
                    };

                    ChannelOperationRequest thirdOpRequest = new()
                    {
                        Description = "Third",
                        RequestType = OperationRequestType.Close,
                        SourceNodeId = carol.Id,
                        DestNodeId = alice.Id,
                        Amount = 2,
                        AmountCryptoUnit = "Sat",
                        Status = ChannelOperationRequestStatus.Pending,
                        CreationDatetime = DateTimeOffset.UtcNow,
                        UpdateDatetime = DateTimeOffset.UtcNow,
                        WalletId = clovrWallet.Id,
                        ChannelOperationRequestSignatures = new List<ChannelOperationRequestSignature>()
                        {
                            new()
                            {
                                PSBT = "PSBT0123",
                                ChannelOpenRequestId = 1,

                                CreationDatetime = DateTimeOffset.Now,
                                UpdateDatetime = DateTimeOffset.Now,
                                SignatureContent = "aslkjsalkjflafjalksjklajdla"
                            },
                            new()
                            {
                                PSBT = "PSBT456",
                                ChannelOpenRequestId = 2,

                                CreationDatetime = DateTimeOffset.Now,
                                UpdateDatetime = DateTimeOffset.Now,
                                SignatureContent = "poweirpowiefposidfpodifopi"
                            }
                        }
                    };
                    
                    theBoss.ChannelOperationRequests.Add(firstOpRequest);
                    someoneElse.ChannelOperationRequests.Add(secondOpRequest);
                    someoneElse.ChannelOperationRequests.Add(thirdOpRequest);

                    _ = Task.Run(() => appUserRepository.Update(theBoss)).Result;
                    _ = Task.Run(() => appUserRepository.Update(someoneElse)).Result;
                    
                }
            }
            else
            {
            }
            applicationDbContext.SaveChanges();
        }

        private static void SetRoles(ApplicationDbContext applicationDbContext)
        {
            const ApplicationUserRole nodeManager = ApplicationUserRole.NodeManager;

            var roles = applicationDbContext.Roles.ToList();
            if (roles.FirstOrDefault(x => x.Name == nodeManager.ToString("G")) == null)
            {
                applicationDbContext.Roles.Add(new IdentityRole
                {
                    Name = nodeManager.ToString("G"),
                    NormalizedName = nodeManager.ToString("G").ToUpper()
                });
            }

            const ApplicationUserRole superadmin = ApplicationUserRole.Superadmin;

            if (roles.FirstOrDefault(x => x.Name == superadmin.ToString("G")) == null)
            {
                applicationDbContext.Roles.Add(new IdentityRole
                {
                    Name = superadmin.ToString("G"),
                    NormalizedName = superadmin.ToString("G").ToUpper()
                });
            }

            const ApplicationUserRole trustedFinanceUser = ApplicationUserRole.TrustedFinanceUser;

            if (roles.FirstOrDefault(x => x.Name == trustedFinanceUser.ToString("G")) == null)
            {
                applicationDbContext.Roles.Add(new IdentityRole
                {
                    Name = trustedFinanceUser.ToString("G"),
                    NormalizedName = trustedFinanceUser.ToString("G").ToUpper()
                });
            }
        }
    }
}