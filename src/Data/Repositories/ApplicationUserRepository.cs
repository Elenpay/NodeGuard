using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ApplicationUserRepository : IApplicationUserRepository
    {
        private readonly IRepository<ApplicationUser> _repository;
        private readonly ILogger<ApplicationUserRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public ApplicationUserRepository(IRepository<ApplicationUser> repository,
            ILogger<ApplicationUserRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<ApplicationUser?> GetByUsername(string username)
        {
            await using var applicationDbContext = _dbContextFactory.CreateDbContext();

            if (username == null) throw new ArgumentNullException(nameof(username));

            try
            {
                return applicationDbContext.ApplicationUsers
                    .SingleOrDefault(x => x.NormalizedUserName == username.ToUpper());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on repository");
                return null;
            }
        }

        public async Task<List<ApplicationUser>> GetUsersInRole(ApplicationUserRole applicationUserRole)
        {
            var roleName = applicationUserRole.ToString("G");
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            //LINQ to SQL query, UserManager should not be used on a blazor component.
            var usersInRoleAsync = from userRoles in applicationDbContext.Set<IdentityUserRole<string>>()
                                   join role in applicationDbContext.Set<IdentityRole>()
                                       on userRoles.RoleId equals role.Id
                                   join user in applicationDbContext.Set<ApplicationUser>().Include(x=> x.Keys)
                                       on userRoles.UserId equals user.Id
                                   where role.NormalizedName == roleName.ToUpper()
                                   select user;

            var result = usersInRoleAsync.ToList();

            return result;
        }

        public async Task<ApplicationUser?> GetById(string id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ApplicationUsers
                .Include(user => user.Keys)
                .FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<ApplicationUser>> GetAll(bool includeBanned = false)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var result = new List<ApplicationUser>();
            if (includeBanned)
            {
                result = await applicationDbContext.ApplicationUsers.Include(user => user.Keys).ToListAsync();
            }
            else
            {
                result = await applicationDbContext.ApplicationUsers.Include(user => user.Keys).Where(x => x.LockoutEnd <= DateTimeOffset.UtcNow || x.LockoutEnd == null).ToListAsync();
            }

            return result;
        }

        public async Task<(bool, string?)> AddAsync(ApplicationUser type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<ApplicationUser> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(ApplicationUser type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<ApplicationUser> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(ApplicationUser type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Update(type, applicationDbContext);
        }
    }
}