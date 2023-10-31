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

﻿// <auto-generated />
using System;
using NodeGuard.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NodeGuard.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20220916170036_Withdrawals")]
    partial class Withdrawals
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "6.0.8")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("ApplicationUserNode", b =>
                {
                    b.Property<int>("NodesId")
                        .HasColumnType("integer");

                    b.Property<string>("UsersId")
                        .HasColumnType("text");

                    b.HasKey("NodesId", "UsersId");

                    b.HasIndex("UsersId");

                    b.ToTable("ApplicationUserNode");
                });

            modelBuilder.Entity("ChannelOperationRequestFMUTXO", b =>
                {
                    b.Property<int>("ChannelOperationRequestsId")
                        .HasColumnType("integer");

                    b.Property<int>("UtxosId")
                        .HasColumnType("integer");

                    b.HasKey("ChannelOperationRequestsId", "UtxosId");

                    b.HasIndex("UtxosId");

                    b.ToTable("ChannelOperationRequestFMUTXO");
                });

            modelBuilder.Entity("FMUTXOWalletWithdrawalRequest", b =>
                {
                    b.Property<int>("UTXOsId")
                        .HasColumnType("integer");

                    b.Property<int>("WalletWithdrawalRequestsId")
                        .HasColumnType("integer");

                    b.HasKey("UTXOsId", "WalletWithdrawalRequestsId");

                    b.HasIndex("WalletWithdrawalRequestsId");

                    b.ToTable("FMUTXOWalletWithdrawalRequest");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Channel", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("BtcCloseAddress")
                        .HasColumnType("text");

                    b.Property<string>("ChannelId")
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("FundingTx")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<long>("FundingTxOutputIndex")
                        .HasColumnType("bigint");

                    b.Property<long>("SatsAmount")
                        .HasColumnType("bigint");

                    b.Property<int>("Status")
                        .HasColumnType("integer");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.ToTable("Channels");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.ChannelOperationRequest", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("AmountCryptoUnit")
                        .HasColumnType("integer");

                    b.Property<int?>("ChannelId")
                        .HasColumnType("integer");

                    b.Property<string>("ClosingReason")
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Description")
                        .HasColumnType("text");

                    b.Property<int?>("DestNodeId")
                        .HasColumnType("integer");

                    b.Property<bool>("IsChannelPrivate")
                        .HasColumnType("boolean");

                    b.Property<string>("JobId")
                        .HasColumnType("text");

                    b.Property<int>("RequestType")
                        .HasColumnType("integer");

                    b.Property<long>("SatsAmount")
                        .HasColumnType("bigint");

                    b.Property<int>("SourceNodeId")
                        .HasColumnType("integer");

                    b.Property<int>("Status")
                        .HasColumnType("integer");

                    b.Property<string>("TxId")
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("UserId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<int>("WalletId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("ChannelId");

                    b.HasIndex("DestNodeId");

                    b.HasIndex("SourceNodeId");

                    b.HasIndex("UserId");

                    b.HasIndex("WalletId");

                    b.ToTable("ChannelOperationRequests");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.ChannelOperationRequestPSBT", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("ChannelOperationRequestId")
                        .HasColumnType("integer");

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<bool>("IsFinalisedPSBT")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsInternalWalletPSBT")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsTemplatePSBT")
                        .HasColumnType("boolean");

                    b.Property<string>("PSBT")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("UserSignerId")
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("ChannelOperationRequestId");

                    b.HasIndex("UserSignerId");

                    b.ToTable("ChannelOperationRequestPSBTs");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.FMUTXO", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<long>("OutputIndex")
                        .HasColumnType("bigint");

                    b.Property<long>("SatsAmount")
                        .HasColumnType("bigint");

                    b.Property<string>("TxId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.ToTable("FMUTXOs");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.InternalWallet", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("DerivationPath")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("MnemonicString")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.ToTable("InternalWallets");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Key", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Description")
                        .HasColumnType("text");

                    b.Property<bool>("IsArchived")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsCompromised")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsFundsManagerPrivateKey")
                        .HasColumnType("boolean");

                    b.Property<string>("MasterFingerprint")
                        .HasColumnType("text");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("Path")
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("UserId")
                        .HasColumnType("text");

                    b.Property<string>("XPUB")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("UserId");

                    b.ToTable("Keys");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Node", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("ChannelAdminMacaroon")
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Description")
                        .HasColumnType("text");

                    b.Property<string>("Endpoint")
                        .HasColumnType("text");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("PubKey")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<int?>("ReturningFundsMultisigWalletId")
                        .HasColumnType("integer");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("Name")
                        .IsUnique();

                    b.HasIndex("PubKey")
                        .IsUnique();

                    b.HasIndex("ReturningFundsMultisigWalletId");

                    b.ToTable("Nodes");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Wallet", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Description")
                        .HasColumnType("text");

                    b.Property<int>("InternalWalletId")
                        .HasColumnType("integer");

                    b.Property<bool>("IsArchived")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsCompromised")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsFinalised")
                        .HasColumnType("boolean");

                    b.Property<int>("MofN")
                        .HasColumnType("integer");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int>("WalletAddressType")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("InternalWalletId");

                    b.ToTable("Wallets");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.WalletWithdrawalRequest", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<decimal>("Amount")
                        .HasColumnType("numeric");

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Description")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("DestinationAddress")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("JobId")
                        .HasColumnType("text");

                    b.Property<string>("RejectCancelDescription")
                        .HasColumnType("text");

                    b.Property<int>("Status")
                        .HasColumnType("integer");

                    b.Property<string>("TxId")
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("UserRequestorId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<int>("WalletId")
                        .HasColumnType("integer");

                    b.Property<bool>("WithdrawAllFunds")
                        .HasColumnType("boolean");

                    b.HasKey("Id");

                    b.HasIndex("UserRequestorId");

                    b.HasIndex("WalletId");

                    b.ToTable("WalletWithdrawalRequests");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.WalletWithdrawalRequestPSBT", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreationDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<bool>("IsFinalisedPSBT")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsInternalWalletPSBT")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsTemplatePSBT")
                        .HasColumnType("boolean");

                    b.Property<string>("PSBT")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("SignerId")
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("UpdateDatetime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int>("WalletWithdrawalRequestId")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.HasIndex("SignerId");

                    b.HasIndex("WalletWithdrawalRequestId");

                    b.ToTable("WalletWithdrawalRequestPSBTs");
                });

            modelBuilder.Entity("KeyWallet", b =>
                {
                    b.Property<int>("KeysId")
                        .HasColumnType("integer");

                    b.Property<int>("WalletsId")
                        .HasColumnType("integer");

                    b.HasKey("KeysId", "WalletsId");

                    b.HasIndex("WalletsId");

                    b.ToTable("KeyWallet");
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRole", b =>
                {
                    b.Property<string>("Id")
                        .HasColumnType("text");

                    b.Property<string>("ConcurrencyStamp")
                        .IsConcurrencyToken()
                        .HasColumnType("text");

                    b.Property<string>("Name")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)");

                    b.Property<string>("NormalizedName")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)");

                    b.HasKey("Id");

                    b.HasIndex("NormalizedName")
                        .IsUnique()
                        .HasDatabaseName("RoleNameIndex");

                    b.ToTable("AspNetRoles", (string)null);
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("ClaimType")
                        .HasColumnType("text");

                    b.Property<string>("ClaimValue")
                        .HasColumnType("text");

                    b.Property<string>("RoleId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("RoleId");

                    b.ToTable("AspNetRoleClaims", (string)null);
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUser", b =>
                {
                    b.Property<string>("Id")
                        .HasColumnType("text");

                    b.Property<int>("AccessFailedCount")
                        .HasColumnType("integer");

                    b.Property<string>("ConcurrencyStamp")
                        .IsConcurrencyToken()
                        .HasColumnType("text");

                    b.Property<string>("Discriminator")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("Email")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)");

                    b.Property<bool>("EmailConfirmed")
                        .HasColumnType("boolean");

                    b.Property<bool>("LockoutEnabled")
                        .HasColumnType("boolean");

                    b.Property<DateTimeOffset?>("LockoutEnd")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("NormalizedEmail")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)");

                    b.Property<string>("NormalizedUserName")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)");

                    b.Property<string>("PasswordHash")
                        .HasColumnType("text");

                    b.Property<string>("PhoneNumber")
                        .HasColumnType("text");

                    b.Property<bool>("PhoneNumberConfirmed")
                        .HasColumnType("boolean");

                    b.Property<string>("SecurityStamp")
                        .HasColumnType("text");

                    b.Property<bool>("TwoFactorEnabled")
                        .HasColumnType("boolean");

                    b.Property<string>("UserName")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)");

                    b.HasKey("Id");

                    b.HasIndex("NormalizedEmail")
                        .HasDatabaseName("EmailIndex");

                    b.HasIndex("NormalizedUserName")
                        .IsUnique()
                        .HasDatabaseName("UserNameIndex");

                    b.ToTable("AspNetUsers", (string)null);

                    b.HasDiscriminator<string>("Discriminator").HasValue("IdentityUser");
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserClaim<string>", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("ClaimType")
                        .HasColumnType("text");

                    b.Property<string>("ClaimValue")
                        .HasColumnType("text");

                    b.Property<string>("UserId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("UserId");

                    b.ToTable("AspNetUserClaims", (string)null);
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserLogin<string>", b =>
                {
                    b.Property<string>("LoginProvider")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<string>("ProviderKey")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<string>("ProviderDisplayName")
                        .HasColumnType("text");

                    b.Property<string>("UserId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("LoginProvider", "ProviderKey");

                    b.HasIndex("UserId");

                    b.ToTable("AspNetUserLogins", (string)null);
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserRole<string>", b =>
                {
                    b.Property<string>("UserId")
                        .HasColumnType("text");

                    b.Property<string>("RoleId")
                        .HasColumnType("text");

                    b.HasKey("UserId", "RoleId");

                    b.HasIndex("RoleId");

                    b.ToTable("AspNetUserRoles", (string)null);
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserToken<string>", b =>
                {
                    b.Property<string>("UserId")
                        .HasColumnType("text");

                    b.Property<string>("LoginProvider")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<string>("Name")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<string>("Value")
                        .HasColumnType("text");

                    b.HasKey("UserId", "LoginProvider", "Name");

                    b.ToTable("AspNetUserTokens", (string)null);
                });

            modelBuilder.Entity("NodeGuard.Data.Models.ApplicationUser", b =>
                {
                    b.HasBaseType("Microsoft.AspNetCore.Identity.IdentityUser");

                    b.HasDiscriminator().HasValue("ApplicationUser");
                });

            modelBuilder.Entity("ApplicationUserNode", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.Node", null)
                        .WithMany()
                        .HasForeignKey("NodesId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NodeGuard.Data.Models.ApplicationUser", null)
                        .WithMany()
                        .HasForeignKey("UsersId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("ChannelOperationRequestFMUTXO", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.ChannelOperationRequest", null)
                        .WithMany()
                        .HasForeignKey("ChannelOperationRequestsId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NodeGuard.Data.Models.FMUTXO", null)
                        .WithMany()
                        .HasForeignKey("UtxosId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("FMUTXOWalletWithdrawalRequest", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.FMUTXO", null)
                        .WithMany()
                        .HasForeignKey("UTXOsId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NodeGuard.Data.Models.WalletWithdrawalRequest", null)
                        .WithMany()
                        .HasForeignKey("WalletWithdrawalRequestsId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("NodeGuard.Data.Models.ChannelOperationRequest", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.Channel", "Channel")
                        .WithMany("ChannelOperationRequests")
                        .HasForeignKey("ChannelId");

                    b.HasOne("NodeGuard.Data.Models.Node", "DestNode")
                        .WithMany("ChannelOperationRequestsAsDestination")
                        .HasForeignKey("DestNodeId");

                    b.HasOne("NodeGuard.Data.Models.Node", "SourceNode")
                        .WithMany("ChannelOperationRequestsAsSource")
                        .HasForeignKey("SourceNodeId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NodeGuard.Data.Models.ApplicationUser", "User")
                        .WithMany("ChannelOperationRequests")
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NodeGuard.Data.Models.Wallet", "Wallet")
                        .WithMany("ChannelOperationRequestsAsSource")
                        .HasForeignKey("WalletId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Channel");

                    b.Navigation("DestNode");

                    b.Navigation("SourceNode");

                    b.Navigation("User");

                    b.Navigation("Wallet");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.ChannelOperationRequestPSBT", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.ChannelOperationRequest", "ChannelOperationRequest")
                        .WithMany("ChannelOperationRequestPsbts")
                        .HasForeignKey("ChannelOperationRequestId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NodeGuard.Data.Models.ApplicationUser", "UserSigner")
                        .WithMany()
                        .HasForeignKey("UserSignerId");

                    b.Navigation("ChannelOperationRequest");

                    b.Navigation("UserSigner");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Key", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.ApplicationUser", "User")
                        .WithMany("Keys")
                        .HasForeignKey("UserId");

                    b.Navigation("User");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Node", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.Wallet", "ReturningFundsMultisigWallet")
                        .WithMany()
                        .HasForeignKey("ReturningFundsMultisigWalletId");

                    b.Navigation("ReturningFundsMultisigWallet");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Wallet", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.InternalWallet", "InternalWallet")
                        .WithMany()
                        .HasForeignKey("InternalWalletId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("InternalWallet");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.WalletWithdrawalRequest", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.ApplicationUser", "UserRequestor")
                        .WithMany("WalletWithdrawalRequests")
                        .HasForeignKey("UserRequestorId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NodeGuard.Data.Models.Wallet", "Wallet")
                        .WithMany()
                        .HasForeignKey("WalletId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("UserRequestor");

                    b.Navigation("Wallet");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.WalletWithdrawalRequestPSBT", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.ApplicationUser", "Signer")
                        .WithMany()
                        .HasForeignKey("SignerId");

                    b.HasOne("NodeGuard.Data.Models.WalletWithdrawalRequest", "WalletWithdrawalRequest")
                        .WithMany("WalletWithdrawalRequestPSBTs")
                        .HasForeignKey("WalletWithdrawalRequestId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Signer");

                    b.Navigation("WalletWithdrawalRequest");
                });

            modelBuilder.Entity("KeyWallet", b =>
                {
                    b.HasOne("NodeGuard.Data.Models.Key", null)
                        .WithMany()
                        .HasForeignKey("KeysId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NodeGuard.Data.Models.Wallet", null)
                        .WithMany()
                        .HasForeignKey("WalletsId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>", b =>
                {
                    b.HasOne("Microsoft.AspNetCore.Identity.IdentityRole", null)
                        .WithMany()
                        .HasForeignKey("RoleId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserClaim<string>", b =>
                {
                    b.HasOne("Microsoft.AspNetCore.Identity.IdentityUser", null)
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserLogin<string>", b =>
                {
                    b.HasOne("Microsoft.AspNetCore.Identity.IdentityUser", null)
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserRole<string>", b =>
                {
                    b.HasOne("Microsoft.AspNetCore.Identity.IdentityRole", null)
                        .WithMany()
                        .HasForeignKey("RoleId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Microsoft.AspNetCore.Identity.IdentityUser", null)
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserToken<string>", b =>
                {
                    b.HasOne("Microsoft.AspNetCore.Identity.IdentityUser", null)
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Channel", b =>
                {
                    b.Navigation("ChannelOperationRequests");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.ChannelOperationRequest", b =>
                {
                    b.Navigation("ChannelOperationRequestPsbts");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Node", b =>
                {
                    b.Navigation("ChannelOperationRequestsAsDestination");

                    b.Navigation("ChannelOperationRequestsAsSource");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.Wallet", b =>
                {
                    b.Navigation("ChannelOperationRequestsAsSource");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.WalletWithdrawalRequest", b =>
                {
                    b.Navigation("WalletWithdrawalRequestPSBTs");
                });

            modelBuilder.Entity("NodeGuard.Data.Models.ApplicationUser", b =>
                {
                    b.Navigation("ChannelOperationRequests");

                    b.Navigation("Keys");

                    b.Navigation("WalletWithdrawalRequests");
                });
#pragma warning restore 612, 618
        }
    }
}