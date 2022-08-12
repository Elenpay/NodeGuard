using AutoMapper;
using FundsManager.Data.Models;

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
        }
    }
}