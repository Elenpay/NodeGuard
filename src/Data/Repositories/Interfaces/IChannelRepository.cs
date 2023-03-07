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

﻿using FundsManager.Data.Models;
 using Lnrpc;
 using Channel = FundsManager.Data.Models.Channel;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IChannelRepository
{
    Task<Channel?> GetById(int id);

    Task<List<Channel>> GetAll();

    Task<(bool, string?)> AddAsync(Channel type);

    Task<(bool, string?)> AddRangeAsync(List<Channel> type);

    (bool, string?) Remove(Channel type);

    Task<(bool, string?)> SafeRemove(Channel type, bool forceClose = false);

    (bool, string?) RemoveRange(List<Channel> types);

    (bool, string?) Update(Channel type);
    
    /// <summary>
    /// Marks the channel if it does not exist as closed
    /// </summary>
    /// <param name="channel"></param>
    /// <returns></returns>
    Task<(bool, string?)> MarkAsClosed(Channel channel);
    
            
    /// <summary>
    /// List the channels of a node
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    Task<ListChannelsResponse?> ListChannels(Node node);

    /// <summary>
    /// Retrieves all the channels to/from nodes managed by the user 
    /// </summary>
    /// <param name="loggedUserId"></param>
    /// <returns></returns>
    Task<List<Channel>> GetAllManagedByUserNodes(string loggedUserId);
}