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

using System.Net;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using FundsManager.TestHelpers;
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
            var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = serviceProvider.GetService<RoleManager<IdentityRole>>();

            var nbXplorerNetwork = CurrentNetworkHelper.GetCurrentNetwork();

            var provider = new NBXplorerNetworkProvider(nbXplorerNetwork.ChainName);
            var nbxplorerClient = new ExplorerClient(provider.GetFromCryptoCode(nbXplorerNetwork.NetworkSet.CryptoCode),
                new Uri(Constants.NBXPLORER_URI));

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

            if (webHostEnvironment.IsDevelopment() && !Constants.ENABLE_REMOTE_SIGNER)
            {
                //Miner setup
                var minerRPC = new RPCClient(new NetworkCredential(Constants.NBXPLORER_BTCRPCUSER, Constants.NBXPLORER_BTCRPCPASSWORD), new Uri(Constants.NBXPLORER_BTCRPCURL!),
                    nbXplorerNetwork);
                var factory = new DerivationStrategyFactory(nbXplorerNetwork);
                //Users
                ApplicationUser? adminUser = null;
                ApplicationUser? financeUser = null;
                if (!applicationDbContext.ApplicationUsers.Any())
                {
                    var adminUsername = "admin";
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

                    var nodeFellaUsername = "nodemanager";
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

                    var nodeFellaUsername1 = "nodemanager1";
                    var nodeFella1 = applicationDbContext.ApplicationUsers.FirstOrDefault(x =>
                        x.NormalizedEmail == nodeFellaUsername1.ToUpper());
                    if (nodeFella1 == null)
                    {
                        nodeFella1 = new ApplicationUser
                        {
                            NormalizedUserName = nodeFellaUsername1.ToUpper(),
                            UserName = nodeFellaUsername1,
                            EmailConfirmed = true,
                            Email = nodeFellaUsername1,
                            NormalizedEmail = nodeFellaUsername1.ToUpper(),
                        };
                        _ = Task.Run(() => userManager.CreateAsync(nodeFella1, "Pass9299a8s.asa9")).Result;
                        _ = Task.Run(() =>
                                userManager.AddToRoleAsync(nodeFella1, ApplicationUserRole.NodeManager.ToString("G")))
                            .Result;
                    }

                    var financeUsername = "financemanager";
                    financeUser =
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

                    var financeManager1 = "financemanager1";
                    var financeUser1 =
                        applicationDbContext.ApplicationUsers.FirstOrDefault(x =>
                            x.NormalizedEmail == financeManager1.ToUpper());

                    if (financeUser1 == null)
                    {
                        financeUser1 = new ApplicationUser
                        {
                            NormalizedUserName = financeManager1.ToUpper(),
                            UserName = financeManager1,
                            EmailConfirmed = true,
                            Email = financeManager1,
                            NormalizedEmail = financeManager1.ToUpper(),
                        };
                        _ = Task.Run(() => userManager.CreateAsync(financeUser1, "Pass9299a8s.asa9")).Result;
                        _ = Task.Run(() =>
                                userManager.AddToRoleAsync(financeUser1, ApplicationUserRole.FinanceManager.ToString()))
                            .Result;
                    }
                }
                else
                {
                    adminUser = applicationDbContext.ApplicationUsers.FirstOrDefault(u => u.UserName == "admin");
                    financeUser = applicationDbContext.ApplicationUsers.FirstOrDefault(u => u.UserName == "financemanager");
                }

                var nodes = Task.Run(() => nodeRepository.GetAll()).Result;

                if (!nodes.Any() && Constants.IS_DEV_ENVIRONMENT)
                {
                    //Testing node from Polar (ALICE) LND 0.15.0 -> check devnetwork.zip polar file

                    //Testing node from Polar (ALICE) LND 0.14.3 -> check devnetwork.zip polar file
                    var alice = new Node
                    {
                        ChannelAdminMacaroon =
                            "0201036c6e6402f801030a108be5b2928f746a822b04a9b2848eb0321201301a160a0761646472657373120472656164120577726974651a130a04696e666f120472656164120577726974651a170a08696e766f69636573120472656164120577726974651a210a086d616361726f6f6e120867656e6572617465120472656164120577726974651a160a076d657373616765120472656164120577726974651a170a086f6666636861696e120472656164120577726974651a160a076f6e636861696e120472656164120577726974651a140a057065657273120472656164120577726974651a180a067369676e6572120867656e6572617465120472656164000006208e8b02d4bc0efd4f15a52946c5ef23f2954f8a07ed800733554a11a190cb71b4",
                        Endpoint = Constants.ALICE_HOST, 
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
                        Endpoint = Constants.CAROL_HOST,
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
                
                var internalWallet = applicationDbContext.InternalWallets.FirstOrDefault();
                if (internalWallet == null)
                {
                    //Default Internal Wallet
                    internalWallet = CreateWallet.CreateInternalWallet(logger);
                    
                    applicationDbContext.Add(internalWallet);
                    applicationDbContext.SaveChanges();
                }

                if (!applicationDbContext.Wallets.Any() && adminUser != null)
                {
                    //Wallets
                    var wallet1Seed =
                        "social mango annual basic work brain economy one safe physical junk other toy valid load cook napkin maple runway island oil fan legend stem";
                    var wallet2Seed =
                        "solar goat auto bachelor chronic input twin depth fork scale divorce fury mushroom column image sauce car public artist announce treat spend jacket physical";
        
                    logger?.LogInformation("Wallet 1 seed: {MnemonicString}", wallet1Seed);
                    logger?.LogInformation("Wallet 2 seed: {MnemonicString}", wallet2Seed);
                    
                    var user1Key = CreateWallet.CreateUserKey("Key 1", adminUser.Id, wallet1Seed);
                    var user2Key = CreateWallet.CreateUserKey("Key 2", financeUser.Id, wallet2Seed);
                    
                    var testingLegacyMultisigWallet = CreateWallet.LegacyMultiSig(internalWallet, "1'", user1Key, user2Key); 
                    var testingMultisigWallet = CreateWallet.MultiSig(internalWallet, "0", user1Key, user2Key);
                    var testingSinglesigWallet = CreateWallet.SingleSig(internalWallet, "1");

                    //Now we fund a multisig address of that wallet with the miner (polar)
                    //We mine 10 blocks
                    minerRPC.Generate(10);

                    //2-of-3 multisig by Key 1 and Key 2 from wallet1/wallet2 and the internal wallet

                    var legacyMultisigDerivationStrategy = testingLegacyMultisigWallet.GetDerivationStrategy();
                    var multisigDerivationStrategy = testingMultisigWallet.GetDerivationStrategy();
                    var singlesigDerivationStrategy = testingSinglesigWallet.GetDerivationStrategy();
                    //Nbxplorer tracking of the multisig derivation scheme

                    nbxplorerClient.Track(legacyMultisigDerivationStrategy);
                    nbxplorerClient.Track(multisigDerivationStrategy);
                    nbxplorerClient.Track(singlesigDerivationStrategy);
                    var evts = nbxplorerClient.CreateLongPollingNotificationSession();

                    var legacyMultisigKeyPathInformation = nbxplorerClient.GetUnused(legacyMultisigDerivationStrategy, DerivationFeature.Deposit);
                    var multisigKeyPathInformation = nbxplorerClient.GetUnused(multisigDerivationStrategy, DerivationFeature.Deposit);
                    var singlesigKeyPathInformation = nbxplorerClient.GetUnused(singlesigDerivationStrategy, DerivationFeature.Deposit);
                    var legacyMultisigAddress = legacyMultisigKeyPathInformation.Address;
                    var multisigAddress = multisigKeyPathInformation.Address;
                    var singlesigAddress = singlesigKeyPathInformation.Address;

                    var legacyMultisigFundCoins = Money.Coins(20m); //20BTC
                    var multisigFundCoins = Money.Coins(20m); //20BTC
                    var singlesigFundCoins = Money.Coins(20m); //20BTC

                    minerRPC.SendToAddress(legacyMultisigAddress, legacyMultisigFundCoins);
                    minerRPC.SendToAddress(multisigAddress, multisigFundCoins);
                    minerRPC.SendToAddress(singlesigAddress, singlesigFundCoins);

                    //6 blocks to confirm
                    minerRPC.Generate(6);

                    WaitNbxplorerNotification(evts, legacyMultisigDerivationStrategy);
                    WaitNbxplorerNotification(evts, multisigDerivationStrategy);
                    WaitNbxplorerNotification(evts, singlesigDerivationStrategy);

                    var legacyMultisigBalance = nbxplorerClient.GetBalance(legacyMultisigDerivationStrategy);
                    var multisigBalance = nbxplorerClient.GetBalance(multisigDerivationStrategy);
                    var singleSigbalance = nbxplorerClient.GetBalance(singlesigDerivationStrategy);
                    var legacyMultisigConfirmedBalance = (Money)legacyMultisigBalance.Confirmed;
                    var multisigConfirmedBalance = (Money)multisigBalance.Confirmed;
                    var singlesigConfirmedBalance = (Money)singleSigbalance.Confirmed;
                    if (legacyMultisigConfirmedBalance.ToUnit(MoneyUnit.BTC) < 20)
                    {
                        throw new Exception("The multisig wallet balance is not >= 20BTC");
                    }
                    if (multisigConfirmedBalance.ToUnit(MoneyUnit.BTC) < 20)
                    {
                        throw new Exception("The multisig wallet balance is not >= 20BTC");
                    }
                    if (singlesigConfirmedBalance.ToUnit(MoneyUnit.BTC) < 20)
                    {
                        throw new Exception("The singlesig wallet balance is not >= 20BTC");
                    }

                    applicationDbContext.Add(testingLegacyMultisigWallet);
                    applicationDbContext.Add(testingMultisigWallet);
                    applicationDbContext.Add(testingSinglesigWallet);
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