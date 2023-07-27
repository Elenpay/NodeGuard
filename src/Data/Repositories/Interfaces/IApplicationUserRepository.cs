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

﻿using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IApplicationUserRepository
{
    Task<ApplicationUser?> GetByUsername(string username);

    Task<ApplicationUser?> GetById(string id);

    Task<List<ApplicationUser>> GetAll(bool includeBanned = false);

    Task<(bool, string?)> AddAsync(ApplicationUser type, string? password = null);

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

    /// <summary>
    /// Locks/ban user
    /// </summary>
    /// <param name="applicationUser"></param>
    /// <returns></returns>
    Task<(bool, string?)> LockUser(ApplicationUser applicationUser);

    /// <summary>
    /// Unlocks/unban user
    /// </summary>
    /// <param name="user"></param>
    /// <returns></returns>
    Task<(bool, string?)> UnlockUser(ApplicationUser user);

    /// <summary>
    /// Creates a new user with the provided password with superadmin role
    /// </summary>
    /// <param name="username"></param>
    /// <param name="confirmationPassword"></param>
    Task<(bool,string?)> CreateSuperAdmin(string username, string confirmationPassword);
}