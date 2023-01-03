using System.Text;
using AutoMapper;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Humanizer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ApplicationUserRepository : IApplicationUserRepository
    {
        private readonly IRepository<ApplicationUser> _repository;
        private readonly ILogger<ApplicationUserRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IMapper _mapper;
        private readonly UserManager<ApplicationUser> _userManager;

        public ApplicationUserRepository(IRepository<ApplicationUser> repository,
            ILogger<ApplicationUserRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IMapper mapper, UserManager<ApplicationUser> userManager)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _mapper = mapper;
            _userManager = userManager;
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
                                   join user in applicationDbContext.Set<ApplicationUser>().Include(x => x.Keys)
                                       on userRoles.UserId equals user.Id
                                   where role.NormalizedName == roleName.ToUpper()
                                   select user;

            var result = usersInRoleAsync.ToList();

            return result;
        }

        /// <summary>
        /// Similar to using UserManager but with a ephemeral db context
        /// </summary>
        /// <param name="applicationUser"></param>
        /// <returns></returns>
        public List<ApplicationUserRole> GetUserRoles(ApplicationUser applicationUser)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            //LINQ to SQL query, UserManager should not be used on a blazor component.
            var identityRoles = GetIdentityRoles(applicationUser, applicationDbContext);

            var result = identityRoles.Select(x => (ApplicationUserRole)Enum.Parse(typeof(ApplicationUserRole), x.Name)).ToList();

            return result;
        }

        /// <summary>
        /// Aux method to get IdentityRole list of an User
        /// </summary>
        /// <param name="applicationUser"></param>
        /// <param name="applicationDbContext"></param>
        /// <returns></returns>
        private List<IdentityRole> GetIdentityRoles(ApplicationUser applicationUser, ApplicationDbContext applicationDbContext)
        {
            var usersInRoleAsync = from userRoles in applicationDbContext.Set<IdentityUserRole<string>>()
                                   join role in applicationDbContext.Set<IdentityRole>()
                                       on userRoles.RoleId equals role.Id
                                   join user in applicationDbContext.Set<ApplicationUser>().Include(x => x.Keys)
                                       on userRoles.UserId equals user.Id
                                   where user.Id == applicationUser.Id
                                   select role;

            var identityRoles = usersInRoleAsync.ToList();
            return identityRoles;
        }

        private List<IdentityUserRole<string>> GetIdentityUserRole(ApplicationUser applicationUser, ApplicationDbContext applicationDbContext)
        {
            var usersInRoleAsync = from userRoles in applicationDbContext.Set<IdentityUserRole<string>>()
                                   join role in applicationDbContext.Set<IdentityRole>()
                                       on userRoles.RoleId equals role.Id
                                   join user in applicationDbContext.Set<ApplicationUser>().Include(x => x.Keys)
                                       on userRoles.UserId equals user.Id
                                   where user.Id == applicationUser.Id
                                   select userRoles;

            var identityRoles = usersInRoleAsync.ToList();
            return identityRoles;
        }

        public async Task<(bool, string?)> ClearNodes(ApplicationUser applicationUser)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
            (bool, string?) result = (true, null);

            try
            {
                var user = await applicationDbContext.ApplicationUsers
                    .Include(x => x.Nodes)
                    .SingleOrDefaultAsync(x => x.Id == applicationUser.Id);

                user.Nodes.Clear();

                var rowsChanged = await applicationDbContext.SaveChangesAsync() > 0;
            }
            catch (Exception e)
            {
                var message = $"Error while clearing nodes entity rel from user:{applicationUser.Id}";
                _logger.LogError(message);
                return (false, message);
            }

            applicationUser.Nodes.Clear();

            return result;
        }

        public async Task<string?> GetUserPasswordMagicLink(ApplicationUser applicationUser)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var token = await _userManager.GeneratePasswordResetTokenAsync(applicationUser);

            var tokenBase64 = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var appEndpoint = $"{Environment.GetEnvironmentVariable("FUNDSMANAGER_ENDPOINT")}/Identity/Account/ResetPassword";
            if (appEndpoint == null)
            {
                _logger.LogError("FUNDSMANAGER_ENDPOINT env var not found");
                return null;
            }

            IDictionary<string, string?> keyValuePairs = new Dictionary<string, string?>
            {
                {"code", tokenBase64}
            };

            var url = QueryHelpers.AddQueryString(appEndpoint, keyValuePairs);

            return url;
        }

        public async Task<(bool, string?)> LockUser(ApplicationUser applicationUser)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            applicationUser = _mapper.Map<ApplicationUser, ApplicationUser>(applicationUser);

            applicationUser.LockoutEnabled = true;
            applicationUser.LockoutEnd = DateTimeOffset.MaxValue; //Banned until the eternity 🔥

            //We sign him out of the app

            applicationUser.SecurityStamp = Guid.NewGuid().ToString();

            var updateResult = Update(applicationUser);

            return updateResult;
        }

        public async Task<(bool, string?)> UnlockUser(ApplicationUser applicationUser)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            applicationUser = _mapper.Map<ApplicationUser, ApplicationUser>(applicationUser);

            applicationUser.LockoutEnabled = false;
            applicationUser.LockoutEnd = null;

            var updateResult = Update(applicationUser);

            return updateResult;
        }

        public async Task<(bool, string?)> CreateSuperAdmin(string username, string confirmationPassword)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(username));
            if (string.IsNullOrWhiteSpace(confirmationPassword))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(confirmationPassword));
            
            var superadminAddResult = await AddAsync(new ApplicationUser
            {
                UserName =  username,
                NormalizedUserName = username.ToUpper(),
            }, confirmationPassword);
            
            if (!superadminAddResult.Item1)
            {
                var message = $"Error while creating superadmin user:{username}";
                _logger.LogError(message);
                return (false, message);
            }
            
            var superadmin = await GetByUsername(username);
            if (superadmin != null)
            {
                var selectedRoles = new List<ApplicationUserRole>{ApplicationUserRole.Superadmin};
                
                var addRole = await UpdateUserRoles( selectedRoles, superadmin);
                if (!addRole.Item1)
                {
                    var message = $"Error while adding superadmin role to user:{username}";
                    _logger.LogError(message);
                    return (false, message);
                }
            }

            return (true, null);
        }

        public async Task<(bool, string?)> UpdateUserRoles(IReadOnlyList<ApplicationUserRole> selectedRoles, ApplicationUser user)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            //Automapper to clear collections
            user = _mapper.Map<ApplicationUser, ApplicationUser>(user);

            (bool, string?) result = (true, null);
            try
            {
                await using var tx = await applicationDbContext.Database.BeginTransactionAsync();

                //Remove all roles
                var identityRoles = GetIdentityUserRole(user, applicationDbContext);

                applicationDbContext.RemoveRange(identityRoles);

                await applicationDbContext.SaveChangesAsync();

                //Reset them
                foreach (var applicationUserRole in selectedRoles)
                {
                    var roleId = (await applicationDbContext.Roles.SingleOrDefaultAsync(x =>
                            x.Name == applicationUserRole.ToString()));

                    if (roleId != null)
                    {
                        var role = new IdentityUserRole<string> { RoleId = roleId.Id, UserId = user.Id };

                        await applicationDbContext.UserRoles.AddAsync(role);
                    }
                }

                var rowsChanged = await applicationDbContext.SaveChangesAsync() > 0;

                if (rowsChanged)
                {
                    //We log him out of the app

                    user.SecurityStamp = Guid.NewGuid().ToString();
                    applicationDbContext.Update(user);
                    var updateStampResult = await applicationDbContext.SaveChangesAsync() > 0;
                    if (!updateStampResult)
                    {
                        _logger.LogError("Error while invalidating user security stamp, user id: {UserId}", user.Id);
                    }
                }

                await tx.CommitAsync();

                result.Item1 = rowsChanged;

                if (!result.Item1)
                {
                    _logger.LogError("Error while updating user roles, user Id: {UserId}", user.Id);
                    result.Item2 = "Error while updating user roles";
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while updating user roles, user Id: {UserId}", user.Id);
            }

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
                result = await applicationDbContext.ApplicationUsers
                    .Include(user => user.Keys)
                    .Include(x => x.Nodes)
                    .ToListAsync();
            }
            else
            {
                result = await applicationDbContext.ApplicationUsers
                    .Include(user => user.Keys)
                    .Include(x => x.Nodes)
                    .Where(x => x.LockoutEnd <= DateTimeOffset.UtcNow || x.LockoutEnd == null)
                    .ToListAsync();
            }

            return result;
        }

        public async Task<(bool, string?)> AddAsync(ApplicationUser type, string? password = null)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.NormalizedUserName = type.UserName.ToUpper();

            var userStore = new UserStore<ApplicationUser>(applicationDbContext);

            if (password != null)
            {
                //Hash the password
                var passwordHasher = new PasswordHasher<ApplicationUser>();
                type.PasswordHash = passwordHasher.HashPassword(type, password);
                
            }

            var identityResult = await userStore.CreateAsync(type);

            return (identityResult.Succeeded, identityResult.Errors.Humanize());
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