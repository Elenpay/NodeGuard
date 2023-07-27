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

﻿using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;

namespace NodeGuard.Data.Models
{
    [Flags]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ApplicationUserRole
    {
        NodeManager, FinanceManager, Superadmin
    }

    public class ApplicationUser : IdentityUser
    {
        [NotMapped]
        public bool IsLocked => LockoutEnabled && LockoutEnd != null;

        #region Relationships

        public ICollection<Key> Keys { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequests { get; set; }
        public ICollection<Node> Nodes { get; set; }
        public ICollection<WalletWithdrawalRequest> WalletWithdrawalRequests { get; set; }

        #endregion Relationships
    }
}