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
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.TestHelpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NodeGuard.Data.Repositories;

namespace NodeGuard.Data
{
    public static class DbInitializer
    {
        public static async Task InitializeAsync(IServiceProvider serviceProvider)
        {
            //DI
            var applicationDbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();
            var dataprotectionKeysContext = serviceProvider.GetRequiredService<DataProtectionKeysContext>();
            var appUserRepository = serviceProvider.GetRequiredService<IApplicationUserRepository>();
            var nodeRepository = serviceProvider.GetRequiredService<INodeRepository>();
            var channelOperationRequestRepository =
                serviceProvider.GetRequiredService<IChannelOperationRequestRepository>();
            var walletRepository = serviceProvider.GetRequiredService<IWalletRepository>();
            var keyRepository = serviceProvider.GetRequiredService<IKeyRepository>();
            var liquidityRuleRepository = serviceProvider.GetRequiredService<ILiquidityRuleRepository>();
            var webHostEnvironment = serviceProvider.GetRequiredService<IWebHostEnvironment>();
            var logger = serviceProvider.GetService<ILogger<Program>>();
            //Nbxplorer setup & check
            var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = serviceProvider.GetService<RoleManager<IdentityRole>>();

            if (roleManager == null)
                throw new Exception("Role manager is not initialized");

            var nbXplorerNetwork = CurrentNetworkHelper.GetCurrentNetwork();

            var provider = new NBXplorerNetworkProvider(nbXplorerNetwork.ChainName);
            var nbxplorerClient = new ExplorerClient(provider.GetFromCryptoCode(nbXplorerNetwork.NetworkSet.CryptoCode),
                new Uri(Constants.NBXPLORER_URI));

            while (!nbxplorerClient.GetStatus().IsFullySynched)
            {
                logger!.LogInformation("Waiting for nbxplorer to be synched..");
                Thread.Sleep(1000);
            }

            //Migrations
            var isConnected = false;
            while (isConnected == false)
            {
                try
                {
                    applicationDbContext.Database.Migrate();
                    dataprotectionKeysContext.Database.Migrate();

                    isConnected = true;
                }
                catch (Exception e)
                {
                    throw new Exception("Error while migrating the database", e);
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
                    adminUser = applicationDbContext.ApplicationUsers.Include(a => a.Nodes).FirstOrDefault(u => u.UserName == "admin");
                    financeUser = applicationDbContext.ApplicationUsers.FirstOrDefault(u => u.UserName == "financemanager");
                }

                if (adminUser == null || financeUser == null)
                    throw new Exception("Could't create admin or finance user");

                var nodes = await nodeRepository.GetAll();

                if (Constants.IS_DEV_ENVIRONMENT)
                {
                    try
                    {
                        await minerRPC.UnloadWalletAsync(""); // RPCErrorCode.RPC_WALLET_NOT_FOUND if already unloaded
                        await minerRPC.LoadWalletAsync("default"); // RPCErrorCode.RPC_WALLET_ALREADY_LOADED if already loaded
                    } catch (RPCException e) when (e.RPCCode == RPCErrorCode.RPC_WALLET_ALREADY_LOADED || e.RPCCode == RPCErrorCode.RPC_WALLET_NOT_FOUND)
                    {
                        // Ignore these errors
                    } catch (Exception e)
                    {
                        throw new Exception("Error while loading default wallet in bitcoind", e);
                    }

                    var alice = nodes.FirstOrDefault(n => n.Name == "alice");
                    if (alice == null)
                    {
                        alice = new Node
                        {
                            ChannelAdminMacaroon = Constants.ALICE_MACAROON,
                            Endpoint = Constants.ALICE_HOST,
                            Name = "alice",
                            CreationDatetime = DateTimeOffset.UtcNow,
                            PubKey = Constants.ALICE_PUBKEY,
                            Users = new List<ApplicationUser>(),
                            AutosweepEnabled = false

                        };
                        _ = await nodeRepository.AddAsync(alice);
                    }
                    else
                    {
                        //Update macaroon and endpoint in case they changed
                        alice.ChannelAdminMacaroon = Constants.ALICE_MACAROON;
                        alice.Endpoint = Constants.ALICE_HOST;
                        alice.PubKey = Constants.ALICE_PUBKEY;
                        alice.UpdateDatetime = DateTimeOffset.UtcNow;
                        nodeRepository.Update(alice);
                    }

                    var carol = nodes.FirstOrDefault(n => n.Name == "carol");
                    if (carol == null)
                    {
                        carol = new Node
                        {
                            ChannelAdminMacaroon = Constants.CAROL_MACAROON,
                            Endpoint = Constants.CAROL_HOST,
                            Name = "carol",
                            CreationDatetime = DateTimeOffset.UtcNow,
                            PubKey = Constants.CAROL_PUBKEY,
                            Users = new List<ApplicationUser>(),
                            AutosweepEnabled = false

                        };
                        _ = await nodeRepository.AddAsync(carol);
                    }
                    else
                    {
                        carol.ChannelAdminMacaroon = Constants.CAROL_MACAROON;
                        carol.Endpoint = Constants.CAROL_HOST;
                        carol.PubKey = Constants.CAROL_PUBKEY;
                        carol.LoopEndpoint = Constants.CAROL_LOOP_HOST;
                        carol.LoopMacaroon = Constants.CAROL_LOOP_MACAROON;
                        carol.UpdateDatetime = DateTimeOffset.UtcNow;
                        nodeRepository.Update(carol);
                    }


                    var bob = nodes.FirstOrDefault(n => n.Name == "bob");
                    if (bob == null)
                    {
                        bob = new Node
                        {
                            ChannelAdminMacaroon = Constants.BOB_MACAROON,
                            Endpoint = Constants.BOB_HOST,
                            Name = "bob",
                            CreationDatetime = DateTimeOffset.UtcNow,
                            PubKey = Constants.BOB_PUBKEY,
                            Users = new List<ApplicationUser>(),
                            AutosweepEnabled = false
                        };
                        _ = await nodeRepository.AddAsync(bob);
                    }
                    else
                    {
                        bob.ChannelAdminMacaroon = Constants.BOB_MACAROON;
                        bob.Endpoint = Constants.BOB_HOST;
                        bob.PubKey = Constants.BOB_PUBKEY;
                        bob.LoopEndpoint = Constants.BOB_LOOP_HOST;
                        bob.LoopMacaroon = Constants.BOB_LOOP_MACAROON;
                        bob.UpdateDatetime = DateTimeOffset.UtcNow;
                        nodeRepository.Update(bob);
                    }

                    if (adminUser.Nodes == null) adminUser.Nodes = [];
                    if (!adminUser.Nodes.Any(n => n.Name == alice.Name)) adminUser.Nodes.Add(alice);
                    if (!adminUser.Nodes.Any(n => n.Name == bob.Name)) adminUser.Nodes.Add(bob);
                    if (!adminUser.Nodes.Any(n => n.Name == carol.Name)) adminUser.Nodes.Add(carol);

                    _ = await applicationDbContext.SaveChangesAsync();
                }

                var internalWallet = applicationDbContext.InternalWallets.FirstOrDefault();
                if (internalWallet == null)
                {
                    //Default Internal Wallet
                    internalWallet = CreateWallet.CreateInternalWallet(logger);

                    applicationDbContext.Add(internalWallet);
                    await applicationDbContext.SaveChangesAsync();
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
                    var testingSingleSigBIP39Wallet = CreateWallet.BIP39Singlesig();

                    //Now we fund a multisig address of that wallet with the miner (polar)
                    //We mine 10 blocks
                    minerRPC.Generate(10);

                    //2-of-3 multisig by Key 1 and Key 2 from wallet1/wallet2 and the internal wallet

                    var legacyMultisigDerivationStrategy = testingLegacyMultisigWallet.GetDerivationStrategy();
                    var multisigDerivationStrategy = testingMultisigWallet.GetDerivationStrategy();
                    var singlesigDerivationStrategy = testingSinglesigWallet.GetDerivationStrategy();
                    var singleSigBIP39DerivationStrategy = testingSingleSigBIP39Wallet.GetDerivationStrategy();

                    if (legacyMultisigDerivationStrategy == null || multisigDerivationStrategy == null ||
                        singlesigDerivationStrategy == null || singleSigBIP39DerivationStrategy == null)
                        throw new Exception("Can't get derivation strategy of the wallets");
                    //Nbxplorer tracking of the multisig derivation scheme

                    nbxplorerClient.Track(legacyMultisigDerivationStrategy);
                    nbxplorerClient.Track(multisigDerivationStrategy);
                    nbxplorerClient.Track(singlesigDerivationStrategy);
                    nbxplorerClient.Track(singleSigBIP39DerivationStrategy);
                    var evts = nbxplorerClient.CreateLongPollingNotificationSession();

                    var legacyMultisigKeyPathInformation = nbxplorerClient.GetUnused(legacyMultisigDerivationStrategy, DerivationFeature.Deposit);
                    var multisigKeyPathInformation = nbxplorerClient.GetUnused(multisigDerivationStrategy, DerivationFeature.Deposit);
                    var singlesigKeyPathInformation = nbxplorerClient.GetUnused(singlesigDerivationStrategy, DerivationFeature.Deposit);
                    var singleSigBIP39KeyPathInformation = nbxplorerClient.GetUnused(singleSigBIP39DerivationStrategy, DerivationFeature.Deposit);
                    var legacyMultisigAddress = legacyMultisigKeyPathInformation.Address;
                    var multisigAddress = multisigKeyPathInformation.Address;
                    var singlesigAddress = singlesigKeyPathInformation.Address;
                    var singleSigBIP39Address = singleSigBIP39KeyPathInformation.Address;

                    var legacyMultisigFundCoins = Money.Coins(20m); //20BTC
                    var multisigFundCoins = Money.Coins(20m); //20BTC
                    var singlesigFundCoins = Money.Coins(20m); //20BTC
                    var singleSigBIP39FundCoins = Money.Coins(20m); //20BTC

                    var lastEventId = evts.LastEventId;
                    logger?.LogInformation(lastEventId.ToString());

                    minerRPC.SendToAddress(legacyMultisigAddress, legacyMultisigFundCoins);
                    minerRPC.SendToAddress(multisigAddress, multisigFundCoins);
                    minerRPC.SendToAddress(singlesigAddress, singlesigFundCoins);
                    minerRPC.SendToAddress(singleSigBIP39Address, singleSigBIP39FundCoins);

                    //6 blocks to confirm
                    minerRPC.Generate(6);

                    var notification1 = WaitNbxplorerNotification(evts, legacyMultisigDerivationStrategy, lastEventId);
                    var notification2 = WaitNbxplorerNotification(evts, multisigDerivationStrategy, lastEventId);
                    var notification3 = WaitNbxplorerNotification(evts, singlesigDerivationStrategy, lastEventId);
                    var notification4 = WaitNbxplorerNotification(evts, singleSigBIP39DerivationStrategy, lastEventId);
                    if (notification1 == null || notification2 == null || notification3 == null ||
                        notification4 == null)
                    {
                        throw new Exception("Wallets are not initialized");
                    }

                    var legacyMultisigBalance = nbxplorerClient.GetBalance(legacyMultisigDerivationStrategy);
                    var multisigBalance = nbxplorerClient.GetBalance(multisigDerivationStrategy);
                    var singleSigbalance = nbxplorerClient.GetBalance(singlesigDerivationStrategy);
                    var singleSigBIP39Balance = nbxplorerClient.GetBalance(singleSigBIP39DerivationStrategy);
                    var legacyMultisigConfirmedBalance = (Money)legacyMultisigBalance.Confirmed;
                    var multisigConfirmedBalance = (Money)multisigBalance.Confirmed;
                    var singlesigConfirmedBalance = (Money)singleSigbalance.Confirmed;
                    var singleSigBIP39ConfirmedBalance = (Money)singleSigBIP39Balance.Confirmed;
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
                    if (singleSigBIP39ConfirmedBalance.ToUnit(MoneyUnit.BTC) < 20)
                    {
                        throw new Exception("The singleSigBIP39 wallet balance is not >= 20BTC");
                    }

                    applicationDbContext.Add(testingLegacyMultisigWallet);
                    applicationDbContext.Add(testingMultisigWallet);
                    applicationDbContext.Add(testingSinglesigWallet);
                    applicationDbContext.Add(testingSingleSigBIP39Wallet);
                }

                // API Tokens generation for services
                var authenticatedServices = new Dictionary<string, string>
                {
                    { "BTCPay", "9Hoz0PMYCsnPUzPO/JbJu8UdaKaAHJsh946xH20UzA0=" },
                    { "X", "C+ktTkMGQupY9LY3IkpyqQQ2pDa7idaeSUKUnm+RawI=" },
                    { "Liquidator", "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=" },
                    { "ElenPay", "2otDr3IrdARnarQZU8RO0ImQko6CDICyLmGUflKUQWA=" }
                };

                var existingTokens = applicationDbContext.ApiTokens.Where(token => authenticatedServices.Keys.Contains(token.Name)).ToList();


                if (existingTokens.Count != authenticatedServices.Count && adminUser != null)
                {
                    foreach (var service in authenticatedServices)
                    {
                        // Check if the service exists in existingTokens
                        if (!existingTokens.Any(token => token.Name == service.Key))
                        {
                            // The service does not exist in existingTokens, so create a new ApiToken
                            var newToken = CreateApiToken(service.Key, service.Value, adminUser.Id);

                            // Add the new token to the database
                            applicationDbContext.ApiTokens.Add(newToken);
                        }
                    }
                }
            }

            await applicationDbContext.SaveChangesAsync();
        }

        private static void SetRoles(RoleManager<IdentityRole> roleManager)
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

        private static APIToken CreateApiToken(string name, string token, string userId)
        {
            var apiToken = new APIToken
            {
                Name = name,
                TokenHash = token,
                IsBlocked = false,
                CreatorId = userId
            };

            apiToken.SetCreationDatetime();
            apiToken.SetUpdateDatetime();

            return apiToken;
        }

        private static NewTransactionEvent? WaitNbxplorerNotification(LongPollingNotificationSession evts, DerivationStrategyBase derivationStrategy, long lastEventId)
        {
            var retryCount = 10;
            while (true)
            {
                var events = evts.GetEvents(lastEventId);
                foreach (var evt in events)
                {
                    if (evt is NewTransactionEvent tx)
                    {
                        if (tx.DerivationStrategy == derivationStrategy)
                            return tx;
                    }
                    lastEventId = evt.EventId;
                }

                if (retryCount-- == 0)
                    break;
                    
                Thread.Sleep(1_000);
            }

            return null;
        }
    }
}
