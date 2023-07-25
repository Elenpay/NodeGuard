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
 */

using System.Text.Json;
using FundsManager.Data.Models;
using FundsManager.Helpers;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext()
        {
        }
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.EnableDetailedErrors();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ChannelOperationRequest>()
                    .HasOne(cor => cor.SourceNode)
                    .WithMany(node => node.ChannelOperationRequestsAsSource)
                    .HasForeignKey(cor => cor.SourceNodeId);
            modelBuilder.Entity<ChannelOperationRequest>()
                .HasOne(cor => cor.DestNode)
                .WithMany(node => node.ChannelOperationRequestsAsDestination)
                .HasForeignKey(cor => cor.DestNodeId);

            modelBuilder.Entity<Node>().HasIndex(x => x.PubKey).IsUnique();
            modelBuilder.Entity<Wallet>().HasIndex(x => new {x.InternalWalletSubDerivationPath, x.InternalWalletMasterFingerprint}).IsUnique();

            //There should be only one Liquidity Rule per Channel
            modelBuilder.Entity<LiquidityRule>().HasIndex(x => x.ChannelId).IsUnique();

            modelBuilder.Entity<ApplicationUser>().HasIndex(x => x.NormalizedUserName).IsUnique();

            base.OnModelCreating(modelBuilder);
        }

        public DbSet<ApplicationUser> ApplicationUsers { get; set; }

        public DbSet<Key> Keys { get; set; }

        public DbSet<Node> Nodes { get; set; }

        public DbSet<Wallet> Wallets { get; set; }

        public DbSet<ChannelOperationRequest> ChannelOperationRequests { get; set; }

        public DbSet<ChannelOperationRequestPSBT> ChannelOperationRequestPSBTs { get; set; }

        public DbSet<WalletWithdrawalRequest> WalletWithdrawalRequests { get; set; }

        public DbSet<WalletWithdrawalRequestPSBT> WalletWithdrawalRequestPSBTs { get; set; }

        public DbSet<Channel> Channels { get; set; }

        public DbSet<InternalWallet> InternalWallets { get; set; }

        public DbSet<FMUTXO> FMUTXOs { get; set; }

        public DbSet<LiquidityRule> LiquidityRules { get; set; }
    }
}