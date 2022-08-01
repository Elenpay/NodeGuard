using System.Net;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Key = FundsManager.Data.Models.Key;

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
            var channelOperationRequestRepository =
                serviceProvider.GetRequiredService<IChannelOperationRequestRepository>();
            var walletRepository = serviceProvider.GetRequiredService<IWalletRepository>();
            var keyRepository = serviceProvider.GetRequiredService<IKeyRepository>();

            var webHostEnvironment = serviceProvider.GetRequiredService<IWebHostEnvironment>();
            var logger = serviceProvider.GetService<ILogger<Program>>();
            //Nbxplorer setup & check
            var nbxplorerUri = Environment.GetEnvironmentVariable("NBXPLORER_URI") ??
                               throw new ArgumentNullException("Environment.GetEnvironmentVariable(\"NBXPLORER_URI\")");
            var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = serviceProvider.GetService<RoleManager<IdentityRole>>();

            var nbXplorerNetwork = CurrentNetworkHelper.GetCurrentNetwork();

            var provider = new NBXplorerNetworkProvider(nbXplorerNetwork.ChainName);
            var nbxplorerClient = new ExplorerClient(provider.GetFromCryptoCode(nbXplorerNetwork.NetworkSet.CryptoCode),
                new Uri(nbxplorerUri));

            while (!nbxplorerClient.GetStatus().IsFullySynched)
            {
                logger!.LogInformation("Waiting for nbxplorer to be synched..");
                Thread.Sleep(100);
            }

            //Migrations
            var isConnected = false;
            while (isConnected == false)
            {
                try
                {
                    applicationDbContext.Database.Migrate();
                    isConnected = true;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while migrating");
                }

                Thread.Sleep(1_000);
            }

            //Roles
            SetRoles(roleManager);

            if (webHostEnvironment.IsDevelopment())
            {
                //Miner setup
                var rpcuser = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCUSER");
                var rpcpassword = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCPASSWORD");
                var rpcuri = Environment.GetEnvironmentVariable("NBXPLORER_BTCRPCURL");
                var minerRPC = new RPCClient(new NetworkCredential(rpcuser, rpcpassword), new Uri(rpcuri!),
                    nbXplorerNetwork);
                var factory = new DerivationStrategyFactory(nbXplorerNetwork);
                //Users
                ApplicationUser? adminUser = null;
                if (!applicationDbContext.ApplicationUsers.Any())
                {
                    var adminUsername = "admin@clovrlabs.com";
                    adminUser = new ApplicationUser
                    {
                        NormalizedUserName = adminUsername.ToUpper(),
                        UserName = adminUsername,
                        EmailConfirmed = true,
                        Email = adminUsername,
                        NormalizedEmail = adminUsername.ToUpper(),
                    };
                    _ = Task.Run(() => userManager.CreateAsync(adminUser, "Pass9299a8s.asa9")).Result;

                    //We are gods with super powers
                    var role1 = Task.Run(() =>
                        userManager.AddToRoleAsync(adminUser, ApplicationUserRole.FinanceManager.ToString("G"))).Result;
                    var role2 = Task.Run(() =>
                        userManager.AddToRoleAsync(adminUser, ApplicationUserRole.NodeManager.ToString("G"))).Result;
                    var role3 = Task.Run(() =>
                        userManager.AddToRoleAsync(adminUser, ApplicationUserRole.Superadmin.ToString("G"))).Result;

                    if (!role1.Succeeded || !role2.Succeeded || !role3.Succeeded)
                    {
                        throw new Exception("Can't set role of admin user");
                    }

                    var nodeFellaUsername = "nodemanager@clovrlabs.com";
                    var nodeFella = applicationDbContext.ApplicationUsers.FirstOrDefault(x =>
                        x.NormalizedEmail == nodeFellaUsername.ToUpper());
                    if (nodeFella == null)
                    {
                        nodeFella = new ApplicationUser
                        {
                            NormalizedUserName = nodeFellaUsername.ToUpper(),
                            UserName = nodeFellaUsername,
                            EmailConfirmed = true,
                            Email = nodeFellaUsername,
                            NormalizedEmail = nodeFellaUsername.ToUpper(),
                        };
                        _ = Task.Run(() => userManager.CreateAsync(nodeFella, "Pass9299a8s.asa9")).Result;
                        _ = Task.Run(() =>
                                userManager.AddToRoleAsync(nodeFella, ApplicationUserRole.NodeManager.ToString("G")))
                            .Result;
                    }

                    var financeUsername = "finance@clovrlabs.com";
                    var financeUser =
                        applicationDbContext.ApplicationUsers.FirstOrDefault(x =>
                            x.NormalizedEmail == financeUsername.ToUpper());

                    if (financeUser == null)
                    {
                        financeUser = new ApplicationUser
                        {
                            NormalizedUserName = financeUsername.ToUpper(),
                            UserName = financeUsername,
                            EmailConfirmed = true,
                            Email = financeUsername,
                            NormalizedEmail = financeUsername.ToUpper(),
                        };
                        _ = Task.Run(() => userManager.CreateAsync(financeUser, "Pass9299a8s.asa9")).Result;
                        _ = Task.Run(() =>
                                userManager.AddToRoleAsync(financeUser, ApplicationUserRole.FinanceManager.ToString()))
                            .Result;
                    }
                }

                var nodes = Task.Run(() => nodeRepository.GetAll()).Result;

                if (!nodes.Any())
                {
                    //Testing node from Polar (ALICE) LND 0.15.0 -> check devnetwork.zip polar file

                    //Testing node from Polar (ALICE) LND 0.14.3 -> check devnetwork.zip polar file
                    var alice = new Node
                    {
                        ChannelAdminMacaroon =
                            "0201036c6e6402f801030a108be5b2928f746a822b04a9b2848eb0321201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e6572617465120472656164000006208e8b02d4bc0efd4f15a52946c5ef23f2954f8a07ed800733554a11a190cb71b4",
                        //THIS MIGHT CHANGE ON YOUR MACHINE!!
                        Endpoint = "host.docker.internal:10001",
                        Name = "Alice",
                        CreationDatetime = DateTimeOffset.UtcNow,
                        PubKey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                        Users = new List<ApplicationUser>()
                    };

                    _ = Task.Run(() => nodeRepository.AddAsync(alice)).Result;

                    //Testing node from Polar (CAROL) LND 0.15.0 -> check devnetwork.zip polar file
                    var carol = new Node
                    {
                        ChannelAdminMacaroon =
                            "0201036c6e6402f801030a10dc64226b045d25f090b114baebcbf04c1201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e657261746512047265616400000620a21b8cc8c071aa5104b706b751aede972f642537c05da31450fb4b02c6da776e",
                        //THIS MIGHT CHANGE ON YOUR MACHINE!!
                        Endpoint = "host.docker.internal:10003",
                        Name = "Carol",
                        CreationDatetime = DateTimeOffset.UtcNow,
                        PubKey = "03485d8dcdd149c87553eeb80586eb2bece874d412e9f117304446ce189955d375",
                        Users = new List<ApplicationUser>()
                    };

                    _ = Task.Run(() => nodeRepository.AddAsync(carol)).Result;

                    //Add user to the channel

                    adminUser.Nodes = new List<Node> { alice, carol };

                    var carolUpdateResult = applicationDbContext.Update(adminUser);
                }
                InternalWallet? internalWallet = null;
                Key? internalWalletKey = null;
                if (!applicationDbContext.InternalWallets.Any())
                {
                    //Default Internal Wallet

                    internalWallet = new InternalWallet
                    {
                        //DerivationPath = "m/48'/1'/1'", //Segwit
                        DerivationPath = Environment.GetEnvironmentVariable("DEFAULT_DERIVATION_PATH")!,
                        MnemonicString =
                            "middle teach digital prefer fiscal theory syrup enter crash muffin easily anxiety ill barely eagle swim volume consider dynamic unaware deputy middle into physical",
                        CreationDatetime = DateTimeOffset.Now,
                    };

                    var masterPrivateKey = internalWallet.GetMasterPrivateKey(nbXplorerNetwork);

                    applicationDbContext.Add(internalWallet);
                    applicationDbContext.SaveChanges();

                    logger.LogInformation("Internal wallet setup, seed:{}", internalWallet.MnemonicString);

                    internalWalletKey =
                        new Key
                        {
                            Name = "FundsManager Co-signing Key",
                            XPUB = internalWallet.GetXPUB(nbXplorerNetwork),
                            IsFundsManagerPrivateKey = true
                        };
                }
                else
                {
                    internalWallet = applicationDbContext.InternalWallets.First();
                    //The last one by id
                    //internalWalletKey = Task.Run(() => keyRepository.GetCurrentInternalWalletKey()).Result;
                }

                if (!applicationDbContext.Wallets.Any() && adminUser != null)
                {
                    //Wallets

                    //Individual wallets
                    var wallet1seed =
                        "social mango annual basic work brain economy one safe physical junk other toy valid load cook napkin maple runway island oil fan legend stem";

                    var masterKey1 = new Mnemonic(wallet1seed).DeriveExtKey().GetWif(Network.RegTest);
                    var keyPath1 =
                        new KeyPath(
                            Environment.GetEnvironmentVariable(
                                "DEFAULT_DERIVATION_PATH")); //https://github.com/dgarage/NBXplorer/blob/0595a87f22c142aee6a6e4a0194f75aec4717819/NBXplorer/Controllers/MainController.cs#L1141
                    var accountKey1 = masterKey1.Derive(keyPath1);
                    var bitcoinExtPubKey1 = accountKey1.Neuter();
                    var accountKeyPath1 = new RootedKeyPath(masterKey1.GetPublicKey().GetHDFingerPrint(), keyPath1);

                    var wallet1DerivationScheme = bitcoinExtPubKey1.ToWif();

                    logger.LogInformation("Wallet 1 seed: {}", wallet1seed);

                    var wallet2seed =
                        "solar goat auto bachelor chronic input twin depth fork scale divorce fury mushroom column image sauce car public artist announce treat spend jacket physical";

                    var masterKey2 = new Mnemonic(wallet2seed).DeriveExtKey().GetWif(Network.RegTest);
                    var keyPath2 =
                        new KeyPath(
                            Environment.GetEnvironmentVariable(
                                "DEFAULT_DERIVATION_PATH")); //https://github.com/dgarage/NBXplorer/blob/0595a87f22c142aee6a6e4a0194f75aec4717819/NBXplorer/Controllers/MainController.cs#L1141
                    var accountKey2 = masterKey2.Derive(keyPath2);
                    var bitcoinExtPubKey2 = accountKey2.Neuter();
                    var accountKeyPath2 = new RootedKeyPath(masterKey2.GetPublicKey().GetHDFingerPrint(), keyPath2);

                    var wallet2DerivationScheme = bitcoinExtPubKey2.ToWif();

                    logger.LogInformation("Wallet 2 seed: {}", wallet2seed);

                    var testingMultisigWallet = new Wallet
                    {
                        MofN = 2,
                        Keys = new List<Key>
                        {
                            new Key
                            {
                                Name = "Key 1",
                                UserId = adminUser.Id,
                                XPUB = wallet1DerivationScheme.ToString()
                                //XPUB = wallet1.DerivationScheme.ToString()
                            },
                            new Key
                            {
                                Name = "Key 2",
                                UserId = adminUser.Id,
                                XPUB = wallet2DerivationScheme.ToString()
                                //XPUB = wallet2.DerivationScheme.ToString()
                            },
                            internalWalletKey
                        },
                        Name = "Test wallet",
                        WalletAddressType = WalletAddressType.NativeSegwit,
                        InternalWalletId = internalWallet.Id,
                        IsFinalised = true,
                        CreationDatetime = DateTimeOffset.Now
                    };

                    //Now we fund a multisig address of that wallet with the miner (polar)
                    //We mine 10 blocks
                    minerRPC.Generate(10);

                    //2-of-3 multisig by Key 1 and Key 2 from wallet1/wallet2 and the internal wallet

                    var derivationStrategy = testingMultisigWallet.GetDerivationStrategy();
                    //Nbxplorer tracking of the multisig derivation scheme

                    nbxplorerClient.Track(derivationStrategy);
                    var evts = nbxplorerClient.CreateLongPollingNotificationSession();

                    var keyPathInformation = nbxplorerClient.GetUnused(derivationStrategy, DerivationFeature.Deposit);
                    var multisigAddress = keyPathInformation.Address;

                    var multisigFundCoins = Money.Coins(20m); //20BTC

                    minerRPC.SendToAddress(multisigAddress, multisigFundCoins);

                    //6 blocks to confirm
                    minerRPC.Generate(6);

                    WaitNbxplorerNotification(evts, derivationStrategy);

                    var balance = nbxplorerClient.GetBalance(derivationStrategy);
                    var confirmedBalance = (Money)balance.Confirmed;
                    if (confirmedBalance.ToUnit(MoneyUnit.BTC) < 20)
                    {
                        throw new Exception("The multisig wallet balance is not >= 20BTC");
                    }

                    applicationDbContext.Add(testingMultisigWallet);
                }
            }
            else
            {
                if (!applicationDbContext.InternalWallets.Any())
                {
                    //Default Internal Wallet, for production we generate a whole random new Mnemonic

                    var internalWallet = new InternalWallet
                    {
                        DerivationPath = Environment.GetEnvironmentVariable("DEFAULT_DERIVATION_PATH"),
                        MnemonicString = new Mnemonic(Wordlist.English).ToString(),
                        CreationDatetime = DateTimeOffset.Now,
                    };

                    applicationDbContext.Add(internalWallet);

                    logger.LogInformation("A new internal wallet seed has been generated: {}",
                        internalWallet.MnemonicString);
                }
            }

            applicationDbContext.SaveChanges();
        }

        private static void SetRoles(RoleManager<IdentityRole>? roleManager)
        {
            const ApplicationUserRole nodeManager = ApplicationUserRole.NodeManager;

            var roles = roleManager.Roles.ToList();
            if (roles.FirstOrDefault(x => x.Name == nodeManager.ToString("G")) == null)
            {
                var identityRole = new IdentityRole
                {
                    Name = nodeManager.ToString("G"),
                    NormalizedName = nodeManager.ToString("G").ToUpper()
                };
                var roleCreation = Task.Run(() => roleManager.CreateAsync(identityRole)).Result;
            }

            const ApplicationUserRole superadmin = ApplicationUserRole.Superadmin;

            if (roles.FirstOrDefault(x => x.Name == superadmin.ToString("G")) == null)
            {
                {
                    var identityRole = new IdentityRole
                    {
                        Name = superadmin.ToString("G"),
                        NormalizedName = superadmin.ToString("G").ToUpper()
                    };
                    var roleCreation = Task.Run(() => roleManager.CreateAsync(identityRole)).Result;
                }
            }

            const ApplicationUserRole financeManager = ApplicationUserRole.FinanceManager;

            if (roles.FirstOrDefault(x => x.Name == financeManager.ToString("G")) == null)
            {
                {
                    var identityRole = new IdentityRole
                    {
                        Name = financeManager.ToString("G"),
                        NormalizedName = financeManager.ToString("G").ToUpper()
                    };
                    var roleCreation = Task.Run(() => roleManager.CreateAsync(identityRole)).Result;
                }
            }
        }

        private static NewTransactionEvent WaitNbxplorerNotification(LongPollingNotificationSession evts, DerivationStrategyBase derivationStrategy)
        {
            while (true)
            {
                var evt = evts.NextEvent();
                if (evt is NewTransactionEvent tx)
                {
                    if (tx.DerivationStrategy == derivationStrategy)
                        return tx;
                }
            }
        }
    }
}