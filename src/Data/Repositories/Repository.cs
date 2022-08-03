using FundsManager.Data.Repositories.Interfaces;

namespace FundsManager.Data.Repositories
{
    /// <summary>
    /// Class-less CRUD Entity manager.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class Repository<T> : IRepository<T>
    {
        private readonly ILogger<T> _logger;

        public Repository(ILogger<T> logger)
        {
            _logger = logger;
        }

        public Task<T> GetById(ApplicationDbContext applicationDbContext)
        {
            //TO BE IMPLEMENTED BY EACH REPOSITORY
            throw new NotImplementedException();
        }

        public async Task<List<T>> GetAll(ApplicationDbContext applicationDbContext)
        {
            //TO BE IMPLEMENTED BY EACH REPOSITORY
            throw new NotImplementedException();
        }

        public async Task<(bool, string?)> AddAsync(T type, ApplicationDbContext applicationDbContext)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var rowsChanged = false;
            try
            {
                await applicationDbContext.AddAsync(type);
                rowsChanged = await applicationDbContext.SaveChangesAsync() > 0;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on repository");
            }

            return (rowsChanged, null);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<T> T, ApplicationDbContext applicationDbContext)
        {
            if (T == null) throw new ArgumentNullException(nameof(T));

            var rowsChanged = false;
            try
            {
                await applicationDbContext.AddRangeAsync(T);
                rowsChanged = await applicationDbContext.SaveChangesAsync() > 0;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on repository");
            }

            return (rowsChanged, null);
        }

        public (bool, string) Update(T type, ApplicationDbContext applicationDbContext)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            var rowsChanged = false;
            try
            {
                applicationDbContext.Update(type);
                var saveChanges = applicationDbContext.SaveChanges();
                rowsChanged = saveChanges > 0;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on repository");
            }

            return (rowsChanged, null);
        }

        public (bool, string?) Remove(T type, ApplicationDbContext applicationDbContext)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            bool rowsChanged = false;
            try
            {
                applicationDbContext.Remove(type);
                rowsChanged = applicationDbContext.SaveChanges() > 0;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on repository");
            }

            return (rowsChanged, null);
        }

        public (bool, string?) RemoveRange(List<T> type, ApplicationDbContext applicationDbContext)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            bool rowsChanged = false;
            try
            {
                applicationDbContext.RemoveRange(type);

                rowsChanged = applicationDbContext.SaveChanges() > 0;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on repository");
            }

            return (rowsChanged, null);
        }
    }
}