// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.



using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories
{
    /// <summary>
    /// Class-less CRUD Entity manager.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    ///

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

        public Task<List<T>> GetAll(ApplicationDbContext applicationDbContext)
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

        public async Task<(bool, string?)> AddRangeAsync<T>(List<T> entities, ApplicationDbContext applicationDbContext) where T : class
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            var rowsChanged = false;
            try
            {
                await applicationDbContext.AddRangeAsync(entities);
                rowsChanged = await applicationDbContext.SaveChangesAsync() > 0;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on repository");
                return (false, e.Message);
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
