using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IApplicationUserRepository
{
    Task<ApplicationUser?> GetByUsername(string username);

    Task<ApplicationUser?> GetById(string id);

    Task<List<ApplicationUser>> GetAll(bool includeBanned = false);

    Task<(bool, string?)> AddAsync(ApplicationUser type);

    Task<(bool, string?)> AddRangeAsync(List<ApplicationUser> type);

    (bool, string?) Remove(ApplicationUser type);

    (bool, string?) RemoveRange(List<ApplicationUser> types);

    (bool, string?) Update(ApplicationUser type);

    Task<List<ApplicationUser>> GetUsersInRole(ApplicationUserRole applicationUserRole);

    List<ApplicationUserRole> GetUserRoles(ApplicationUser applicationUser);

    /// <summary>
    /// Updates all the roles to the provided list
    /// </summary>
    /// <param name="selectedRoles"></param>
    /// <param name="user"></param>
    /// <returns></returns>
    Task<(bool, string?)> UpdateUserRoles(IReadOnlyList<ApplicationUserRole> selectedRoles, ApplicationUser user);

    /// <summary>
    /// Used to remove the nodes from the many-to-many rel
    /// </summary>
    /// <param name="applicationUser"></param>
    /// <returns></returns>
    Task<(bool, string?)> ClearNodes(ApplicationUser applicationUser);

    /// <summary>
    /// Generates a full url to set the user's password by a magic link
    /// </summary>
    /// <param name="applicationUser"></param>
    /// <returns></returns>
    Task<string?> GetUserPasswordMagicLink(ApplicationUser applicationUser);
}