/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

﻿namespace FundsManager.Data.Repositories.Interfaces
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