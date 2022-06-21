using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IChannelOperationRequestSignatureRepository
{
    Task<ChannelOperationRequestSignature?> GetById(int id);
    Task<List<ChannelOperationRequestSignature>> GetAll();
    Task<(bool, string?)> AddAsync(ChannelOperationRequestSignature type);
    Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequestSignature> type);
    (bool, string?) Remove(ChannelOperationRequestSignature type);
    (bool, string?) RemoveRange(List<ChannelOperationRequestSignature> types);
    (bool, string?) Update(ChannelOperationRequestSignature type);
}