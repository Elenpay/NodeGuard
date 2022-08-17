using AutoMapper;
using FundsManager.Data.Models;
using NBitcoin;
using UTXO = NBXplorer.Models.UTXO;

namespace FundsManager.Automapper
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
                .ForMember(x => x.ChannelOperationRequests, opt => opt.Ignore());

            CreateMap<Node, Node>()
                .ForMember(x => x.ChannelOperationRequestsAsDestination, opt => opt.Ignore())
                .ForMember(x => x.ChannelOperationRequestsAsSource, opt => opt.Ignore())
                .ForMember(x => x.Users, opt => opt.Ignore());

            CreateMap<ChannelOperationRequest, ChannelOperationRequest>()
                .ForMember(x => x.Utxos, opt => opt.Ignore())
                .ForMember(x => x.Channel, opt => opt.Ignore())
                .ForMember(x => x.User, opt => opt.Ignore())
                .ForMember(x => x.SourceNode, opt => opt.Ignore())
                .ForMember(x => x.DestNode, opt => opt.Ignore())
                .ForMember(x => x.Wallet, opt => opt.Ignore())
                .ForMember(x => x.ChannelOperationRequestPsbts, opt => opt.Ignore());

            CreateMap<UTXO, FMUTXO>()
                .ForMember(x => x.TxId, opt => opt.MapFrom(x => x.Outpoint.Hash.ToString()))
                .ForMember(x => x.OutputIndex, opt => opt.MapFrom(x => x.Outpoint.N))
                .ForMember(x => x.SatsAmount, opt => opt.MapFrom(x => ((Money)x.Value).Satoshi));
        }
    }
}