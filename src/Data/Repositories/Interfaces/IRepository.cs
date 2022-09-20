namespace FundsManager.Data.Repositories.Interfaces
{
    /// <summary>
    /// Interface of a base Repository implementation
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IRepository<T>
    {
        /// <summary>
        /// Gets one entity by its id <see cref="IRepository{T}"></see>
        /// </summary>
        /// <returns>T</returns>
        public Task<T> GetById(ApplicationDbContext applicationDbContext);

        /// <summary>
        /// Gets all the entities in <see cref="IRepository{T}"></see>
        /// </summary>
        /// <returns> A list of <see cref="List{T}"></see></returns>
        public Task<List<T>> GetAll(ApplicationDbContext applicationDbContext);

        /// <summary>
        /// Persist a new entity of  <see cref="IRepository{T}"></see>
        /// </summary>
        /// <param name="type"></param>
        /// <returns>A tuple (bool, string). The bool represents the call success and the string any possible message.</returns>
        public Task<(bool, string?)> AddAsync(T type, ApplicationDbContext applicationDbContext);

        /// <summary>
        /// Persist a new a collection of Entities of  <see cref="IRepository{T}"></see>
        /// </summary>
        /// <param name="T"></param>
        /// <returns>A tuple (bool, string). The bool represents the call success and the string any possible message.</returns>
        public Task<(bool, string?)> AddRangeAsync(List<T> T, ApplicationDbContext applicationDbContext);

        /// <summary>
        ///Removes an existing entity of <see cref="IRepository{T}"></see>
        /// </summary>
        /// <param name="type"></param>
        /// <returns>A tuple (bool, string). The bool represents the call success and the string any possible message.</returns>
        public (bool, string?) Remove(T type, ApplicationDbContext applicationDbContext);

        /// <summary>
        /// Removes a collection of entities of <see cref="IRepository{T}"></see>
        /// </summary>
        /// <param name="type"></param>
        /// <returns>A tuple (bool, string). The bool represents the call success and the string any possible message.</returns>
        public (bool, string?) RemoveRange(List<T> type, ApplicationDbContext applicationDbContext);

        /// <summary>
        /// Updates an existing entity of <see cref="IRepository{T}"></see>
        /// </summary>
        /// <param name="type"></param>
        /// <param name="applicationDbContext"></param>
        /// <returns>A tuple (bool, string). The bool represents the call success and the string any possible message.</returns>
        public (bool, string) Update(T type, ApplicationDbContext applicationDbContext);
    }
}