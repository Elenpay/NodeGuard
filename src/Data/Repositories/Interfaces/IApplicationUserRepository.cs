using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IApplicationUserRepository
{
    Task<ApplicationUser?> GetByUsername(string username);

    Task<ApplicationUser?> GetById(string id);

    Task<List<ApplicationUser>> GetAll();

    Task<(bool, string?)> AddAsync(ApplicationUser type);

    Task<(bool, string?)> AddRangeAsync(List<ApplicationUser> type);

    (bool, string?) Remove(ApplicationUser type);

    (bool, string?) RemoveRange(List<ApplicationUser> types);

    (bool, string?) Update(ApplicationUser type);
}