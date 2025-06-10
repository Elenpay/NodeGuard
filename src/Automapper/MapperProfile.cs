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

using AutoMapper;
using NodeGuard.Data.Models;
using NBitcoin;
using UTXO = NBXplorer.Models.UTXO;

namespace NodeGuard.Automapper
{
    /// <summary>
    /// Profile for setting automapper profile (Mainly used to strip collections when cloning objects)
    /// </summary>
    public class MapperProfile : Profile
    {
        public MapperProfile()
        {
            CreateMap<ApplicationUser, ApplicationUser>()
                .ForMember(x => x.Nodes, opt => opt.Ignore())
                .ForMember(x => x.Keys, opt => opt.Ignore())
                .ForMember(x => x.ChannelOperationRequests, opt => opt.Ignore())
                .ForMember(x => x.WalletWithdrawalRequests, opt => opt.Ignore());

            CreateMap<Node, Node>()
                .ForMember(x => x.ChannelOperationRequestsAsDestination, opt => opt.Ignore())
                .ForMember(x => x.ChannelOperationRequestsAsSource, opt => opt.Ignore())
                .ForMember(x => x.ReturningFundsWallet, opt => opt.Ignore())
                .ForMember(x => x.Users, opt => opt.Ignore());

            CreateMap<Channel, Channel>()
                .ForMember(x => x.ChannelOperationRequests, opt => opt.Ignore())
                .ForMember(x => x.LiquidityRules, opt => opt.Ignore());

            CreateMap<ChannelOperationRequest, ChannelOperationRequest>()
                .ForMember(x => x.Utxos, opt => opt.Ignore())
                .ForMember(x => x.Channel, opt => opt.Ignore())
                .ForMember(x => x.User, opt => opt.Ignore())
                .ForMember(x => x.SourceNode, opt => opt.Ignore())
                .ForMember(x => x.DestNode, opt => opt.Ignore())
                .ForMember(x => x.Wallet, opt => opt.Ignore())
                .ForMember(x => x.ChannelOperationRequestPsbts, opt => opt.Ignore());

            CreateMap<WalletWithdrawalRequest, WalletWithdrawalRequest>()
                .ForMember(x => x.Wallet, opt => opt.Ignore())
                .ForMember(x => x.WalletWithdrawalRequestPSBTs, opt => opt.Ignore())
                .ForMember(x => x.UserRequestor, opt => opt.Ignore())
                .ForMember(x => x.UTXOs, opt => opt.Ignore());

            CreateMap<UTXO, FMUTXO>()
                .ForMember(x => x.TxId, opt => opt.MapFrom(x => x.Outpoint.Hash.ToString()))
                .ForMember(x => x.OutputIndex, opt => opt.MapFrom(x => x.Outpoint.N))
                .ForMember(x => x.SatsAmount, opt => opt.MapFrom(x => ((Money)x.Value).Satoshi));

            CreateMap<LiquidityRule, Nodeguard.LiquidityRule>()
                .ForMember(x => x.MinimumLocalBalance, opt => opt.MapFrom(x => x.MinimumLocalBalance ?? 0))
                .ForMember(x => x.MinimumRemoteBalance, opt => opt.MapFrom(x => x.MinimumRemoteBalance ?? 0))
                .ForMember(x => x.IsReverseSwapWalletRule, opt => opt.MapFrom(x => x.IsReverseSwapWalletRule))
                .ForMember(x => x.ReverseSwapAddress, opt => opt.MapFrom(x => x.ReverseSwapAddress ?? string.Empty))
                .ForMember(x => x.ChannelId, opt => opt.MapFrom(x => x.Channel.ChanId))
                .ForMember(x => x.SwapWalletId, opt => opt.MapFrom(x => x.SwapWalletId))
                .ForMember(x => x.ReverseSwapWalletId, opt => opt.MapFrom(x => x.ReverseSwapWalletId ?? 0))
                .ForMember(x => x.RebalanceTarget, opt => opt.MapFrom(x => x.RebalanceTarget ?? 0))
                .ForMember(x => x.NodePubkey, opt => opt.MapFrom(x => x.Node.PubKey))
                .ForMember(x => x.RemoteNodePubkey, opt => opt.MapFrom(x => x.RemoteNodePubkey ?? string.Empty));

            CreateMap<LiquidityRule, LiquidityRule>()
                .ForMember(x => x.SwapWallet, opt => opt.Ignore())
                .ForMember(x => x.ReverseSwapWallet, opt => opt.Ignore())
                .ForMember(x => x.Channel, opt => opt.Ignore());

            CreateMap<Node, Nodeguard.Node>()
                .ForMember(x => x.Name, opt => opt.MapFrom(x => x.Name ?? string.Empty))
                //Description
                .ForMember(x => x.Description, opt => opt.MapFrom(x => x.Description ?? string.Empty))
                //Pubkey
                .ForMember(x => x.PubKey, opt => opt.MapFrom(x => x.PubKey ?? string.Empty));


        }
    }
}
