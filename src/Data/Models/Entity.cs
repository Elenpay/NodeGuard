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

using System.ComponentModel.DataAnnotations;

namespace FundsManager.Data.Models
{
    public abstract class Entity
    {
        [Key]
        public int Id { get; set; }

        public DateTimeOffset CreationDatetime { get; set; }
        public DateTimeOffset UpdateDatetime { get; set; }

        public virtual void SetCreationDatetime()
        {
            CreationDatetime = DateTimeOffset.UtcNow;
        }

        public virtual void SetUpdateDatetime()
        {
            UpdateDatetime = DateTimeOffset.UtcNow;
        }

        public override string ToString()
        {
            return $"{Id}";
        }
    }
}